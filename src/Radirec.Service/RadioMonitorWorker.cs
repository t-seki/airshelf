using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Radirec.Core.Ingestion;
using Radirec.Hosting;

namespace Radirec.Service;

/// <summary>
/// I: を監視し録音を検知即取り込みする常駐ワーカー。正しさはキャッチアップ + import-once 冪等で
/// 担保する(ADR-0003):①FileSystemWatcher ②スリープ復帰 ③起動時 ④定期、の各トリガを
/// 同じ取り込みパイプラインへ流し、スキャンは常に1本に直列化する。
/// </summary>
public sealed class RadioMonitorWorker : BackgroundService
{
    private readonly RadirecConfig _config;
    private readonly ILogger<RadioMonitorWorker> _log;
    private readonly IngestService _ingest;
    private readonly IDisposable? _resources;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounce;
    private CancellationToken _stopping;

    public RadioMonitorWorker(RadirecConfig config, ILogger<RadioMonitorWorker> log)
    {
        _config = config;
        _log = log;
        (_ingest, _resources) = CompositionRoot.Build(config, dryRun: false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _log.LogInformation("Radirec サービス開始。監視ルート: {Root}", _config.RadioRoot);

        var orphans = _ingest.CleanupOrphans();
        if (orphans > 0)
            _log.LogInformation("中断で残った .partial を {Count} 件掃除しました", orphans);

        StartWatcher();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        await ScanAsync("起動時", stoppingToken);

        // 定期スキャン(安全網。イベント取りこぼし・監視不能時のフォールバック)。
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, _config.ScanIntervalSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ScanAsync("定期", stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 停止要求。正常終了。
        }
        finally
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _watcher?.Dispose();
        }
    }

    private void StartWatcher()
    {
        if (!Directory.Exists(_config.RadioRoot))
        {
            _log.LogWarning("監視ルートが存在しません: {Root}(定期スキャンのみで継続)", _config.RadioRoot);
            return;
        }

        _watcher = new FileSystemWatcher(_config.RadioRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, _) => Debounce();
        _watcher.Changed += (_, _) => Debounce();
        _watcher.Error += (_, e) => _log.LogWarning(e.GetException(), "FileSystemWatcher エラー(定期スキャンで回収)");
        _watcher.EnableRaisingEvents = true;
    }

    // 書き込み中の連続イベントを束ね、静穏化してからスキャンする(安定判定は取り込み側でも実施)。
    private void Debounce()
    {
        _debounce?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        _debounce = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _config.WatchDebounceSeconds)), cts.Token);
                await ScanAsync("検知", _stopping);
            }
            catch (OperationCanceledException)
            {
                // 次のイベントで置き換えられた or 停止。
            }
        }, cts.Token);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // 強制スリープからの復帰。取りこぼしをキャッチアップ(ADR-0003 の主回収機構)。
        if (e.Mode == PowerModes.Resume)
            _ = ScanAsync("スリープ復帰", _stopping);
    }

    // スキャンは常に1本に直列化。実行中の追加トリガはスキップ(定期/次イベントで確実に回収される)。
    private async Task ScanAsync(string trigger, CancellationToken cancellationToken)
    {
        if (!await _scanLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

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
            // 停止要求。
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Trigger}スキャンで想定外の例外", trigger);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public override void Dispose()
    {
        _scanLock.Dispose();
        _resources?.Dispose();
        base.Dispose();
    }
}
