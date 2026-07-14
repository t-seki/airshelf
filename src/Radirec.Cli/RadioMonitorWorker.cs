using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Radirec.Core.Ingestion;
using Radirec.Hosting;
using Radirec.Hosting.Control;

namespace Radirec.Cli;

/// <summary>
/// I: を監視し録音を検知即取り込みする常駐ワーカー。正しさはキャッチアップ + import-once 冪等で
/// 担保する(ADR-0003)。トリガ①FileSystemWatcher ②スリープ復帰(電源イベント)③起動時 ④定期
/// ⑤手動(制御チャネル)はすべて単一の要求チャネルへ投げ、ExecuteAsync 内の**単一直列コンシューマ**が
/// 順に取り込む。これにより fire-and-forget を排し、停止時に在中スキャンをまたいでリソースを破棄する
/// レースを避ける。制御チャネル(ADR-0010)の <see cref="IServiceControl"/> 実装も兼ね、rescan は要求
/// チャネルへ、status はスレッドセーフな状態スナップショットを直接返す(直列チャネルを介さない)。
/// </summary>
public sealed class RadioMonitorWorker : BackgroundService, IServiceControl
{
    private readonly RadirecConfig _config;
    private readonly ILogger<RadioMonitorWorker> _log;
    private readonly IngestService _ingest;
    private readonly IDisposable? _resources;

    // 制御チャネルの status が読む状態。immutable record を volatile 参照で差し替える(単一書き手 =
    // ExecuteAsync スレッド、読み手 = listener スレッド)。ADR-0010。
    private volatile ServiceStatusSnapshot _status;

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
        // 常駐サービスのみ取込要約シグナルを発行する(ADR-0008)。
        (_ingest, _resources) = CompositionRoot.Build(config, dryRun: false, notify: true);
        _status = new ServiceStatusSnapshot
        {
            Version = VersionInfo.Current,
            StartedUtc = TimeProvider.System.GetUtcNow(),
            RadioRoot = config.RadioRoot,
            Watching = false, // StartWatcher 後に更新する
        };
    }

    // --- IServiceControl(制御チャネル。ADR-0010)-----------------------------------------

    /// <summary>手動再スキャン。既存の要求チャネルへ coalesce される冪等操作(受理まで)。</summary>
    public void RequestRescan() => Request("手動");

    /// <summary>ライブ状態のスナップショット(在中スキャン中でも直列チャネルを介さず即応)。</summary>
    public ServiceStatusSnapshot GetStatus() => _status;

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
        _status = _status with { Watching = true };
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
                var notify = report.NotificationPublished ? "発行" : report.NotificationError ?? "-";
                _log.LogInformation(
                    "{Trigger}スキャン: 取込 {Imported} / 失敗 {Failed} / Jellyfin更新 {Refresh} / 通知 {Notify}",
                    trigger, imported, failed, refresh, notify);
            }

            // 走査ごとに status スナップショットを更新(制御チャネル。ADR-0010)。0 件走査でも
            // 「最後にいつ走ったか」を反映する。書き手はこの直列コンシューマ 1 つだけ。
            _status = _status with
            {
                LastScanCompletedUtc = TimeProvider.System.GetUtcNow(),
                LastScanTrigger = trigger,
                LastImported = imported,
                LastFailed = failed,
            };
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
