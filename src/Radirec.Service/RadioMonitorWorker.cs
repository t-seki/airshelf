using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Radirec.Core.Ingestion;
using Radirec.Hosting;

namespace Radirec.Service;

/// <summary>
/// I: を監視し録音を検知即取り込みする常駐ワーカー。正しさはキャッチアップ + import-once 冪等で
/// 担保する(ADR-0003)。トリガ①FileSystemWatcher ②スリープ復帰(電源イベント)③起動時 ④定期は
/// すべて単一の要求チャネルへ投げ、ExecuteAsync 内の**単一直列コンシューマ**が順に取り込む。
/// これにより fire-and-forget を排し、停止時に在中スキャンをまたいでリソースを破棄するレースを避ける。
/// </summary>
public sealed class RadioMonitorWorker : BackgroundService
{
    private readonly RadirecConfig _config;
    private readonly ILogger<RadioMonitorWorker> _log;
    private readonly IngestService _ingest;
    private readonly IDisposable? _resources;

    // 容量1・満杯時は書き捨て = 複数トリガを1回のスキャンに束ねる(コアレス)。
    private readonly Channel<string> _requests = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer; // 書き込み中の連続イベントを束ねる(単一の再利用タイマー)
    private Timer? _periodicTimer; // 定期(安全網)

    public RadioMonitorWorker(RadirecConfig config, ILogger<RadioMonitorWorker> log)
    {
        _config = config;
        _log = log;
        (_ingest, _resources) = CompositionRoot.Build(config, dryRun: false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Radirec サービス開始。監視ルート: {Root}", _config.RadioRoot);

        var orphans = _ingest.CleanupOrphans();
        if (orphans > 0)
            _log.LogInformation("中断で残った .partial を {Count} 件掃除しました", orphans);

        StartWatcher();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _periodicTimer = new Timer(
            _ => Request("定期"), null,
            TimeSpan.FromSeconds(Math.Max(10, _config.ScanIntervalSeconds)),
            TimeSpan.FromSeconds(Math.Max(10, _config.ScanIntervalSeconds)));

        Request("起動時");

        try
        {
            // 単一直列コンシューマ。停止時はこのループが在中スキャン完了後に抜ける(StopAsync が待つ)。
            await foreach (var trigger in _requests.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                await ScanAsync(trigger, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 停止要求。正常終了。
        }
        finally
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }

    private void Request(string trigger) => _requests.Writer.TryWrite(trigger);

    private void StartWatcher()
    {
        if (!Directory.Exists(_config.RadioRoot))
        {
            _log.LogWarning("監視ルートが存在しません: {Root}(定期スキャンのみで継続)", _config.RadioRoot);
            return;
        }

        // 書き込み中の連続イベントを束ね、静穏化してから1回スキャンを要求(安定判定は取り込み側でも実施)。
        _debounceTimer = new Timer(_ => Request("検知"), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _watcher = new FileSystemWatcher(_config.RadioRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, _) => NudgeDebounce();
        _watcher.Changed += (_, _) => NudgeDebounce();
        _watcher.Error += (_, e) => _log.LogWarning(e.GetException(), "FileSystemWatcher エラー(定期スキャンで回収)");
        _watcher.EnableRaisingEvents = true;
    }

    // イベントごとに単一タイマーを再スタート = 最後のイベントから静穏 WatchDebounceSeconds 秒後に発火。
    private void NudgeDebounce()
        => _debounceTimer?.Change(TimeSpan.FromSeconds(Math.Max(1, _config.WatchDebounceSeconds)), Timeout.InfiniteTimeSpan);

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // 強制スリープからの復帰。取りこぼしをキャッチアップ(ADR-0003 の主回収機構)。
        if (e.Mode == PowerModes.Resume)
            Request("スリープ復帰");
    }

    private async Task ScanAsync(string trigger, CancellationToken cancellationToken)
    {
        try
        {
            var report = await _ingest.RunOnceAsync(dryRun: false, cancellationToken);
            var imported = report.Count(IngestOutcome.Imported);
            var failed = report.Count(IngestOutcome.Failed);
            if (imported > 0 || failed > 0)
            {
                var refresh = report.LibraryRefreshed ? "起動" : report.RefreshError ?? "-";
                _log.LogInformation(
                    "{Trigger}スキャン: 取込 {Imported} / 失敗 {Failed} / Jellyfin更新 {Refresh}",
                    trigger, imported, failed, refresh);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // 停止要求はコンシューマループへ伝播させて終了する。
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Trigger}スキャンで想定外の例外", trigger);
        }
    }

    public override void Dispose()
    {
        // コンシューマループ(ExecuteAsync)は StopAsync で完了済み。ここでは資源のみ解放。
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        _periodicTimer?.Dispose();
        _resources?.Dispose();
        base.Dispose();
    }
}
