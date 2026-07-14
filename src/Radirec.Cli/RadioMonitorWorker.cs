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
/// ⑤手動(制御チャネル)⑥リロードはすべて単一の要求チャネルへ投げ、ExecuteAsync 内の**単一直列
/// コンシューマ**が順に処理する。これにより fire-and-forget を排し、停止時に在中スキャンをまたいで
/// リソースを破棄するレースを避ける。制御チャネル(ADR-0010)の <see cref="IServiceControl"/> 実装も
/// 兼ね、rescan は要求チャネルへ、status はスレッドセーフな状態スナップショットを直接返し、reload は
/// **config 由来ランタイム一式(グラフ + watcher + タイマー)をプロセス内で丸ごと再構築**する
/// (部分 hot-swap はしない)。
/// </summary>
public sealed class RadioMonitorWorker : BackgroundService, IServiceControl
{
    private readonly ILogger<RadioMonitorWorker> _log;
    private readonly string _configPath;

    // config 由来のランタイム。reload で丸ごと差し替わるため readonly ではない(書き手は直列ループのみ)。
    private RadirecConfig _config;
    private IngestService _ingest;
    private IDisposable? _resources;

    // 制御チャネルの status が読む状態。immutable record を差し替える(単一書き手 = ExecuteAsync
    // スレッド、読み手 = listener スレッド)。ADR-0010。
    private volatile ServiceStatusSnapshot _status;

    // 容量1・満杯時は書き捨て = 複数トリガを1回のスキャンに束ねる(コアレス)。
    private readonly Channel<string> _requests = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    // reload は冪等でないため上の DropWrite チャネルには乗せず、取りこぼさないスロットで運ぶ(ADR-0010)。
    // 起こしはトリガチャネルへの書き込み。set 後に必ずループが 1 回起きて Take するので取りこぼさない。
    private readonly PendingReloadSlot<RadirecConfig> _pendingReload;

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer; // 書き込み中の連続イベントを束ねる(単一の再利用タイマー)
    private Timer? _periodicTimer; // 定期(安全網)

    public RadioMonitorWorker(RadirecConfig config, ServiceRuntimeOptions options, ILogger<RadioMonitorWorker> log)
    {
        _config = config;
        _configPath = options.ConfigPath;
        _log = log;
        _pendingReload = new PendingReloadSlot<RadirecConfig>(() => Request("リロード"));
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

    /// <summary>
    /// 設定リロード。新 config の読込+構文検証だけ**同期実行**(グラフに触れない)し、妥当なら再構築を
    /// スロットへ投入して即「受理」、不正なら旧構成を維持して失敗を返す。実グラフ差し替えは在中スキャン
    /// 完了後に直列ループ上で <see cref="ApplyReload"/> が行う(ADR-0010)。
    /// </summary>
    public ReloadResult RequestReload()
    {
        if (!ConfigLoading.TryLoad(_configPath, out var newConfig, out var error))
            return ReloadResult.Failed(error!);

        _pendingReload.Set(newConfig!);
        return ReloadResult.Ok();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Radirec サービス開始。監視ルート: {Root}", _config.RadioRoot);

        var orphans = _ingest.CleanupOrphans();
        if (orphans > 0)
            _log.LogInformation("中断で残った .partial を {Count} 件掃除しました", orphans);

        StartWatcher();
        StartPeriodicTimer();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Request("起動時");

        try
        {
            // 単一直列コンシューマ。停止時はこのループが在中スキャン完了後に抜ける(StopAsync が待つ)。
            await foreach (var trigger in _requests.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // 在中スキャン完了後(= 反復の先頭)にのみ reload を適用する。ADR-0010 の直列不変条件を保つ。
                if (_pendingReload.Take() is { } newConfig)
                    ApplyReload(newConfig);

                await ScanAsync(trigger, stoppingToken);
            }
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

    // config 由来ランタイムを新 config で丸ごと再構築する(部分 hot-swap はしない。ADR-0010)。
    // 新グラフを**先に**構築し、成功したときだけ旧構成を破棄して差し替える(build-new-first)。
    // 構築失敗(SQLite オープンや stations/programs 読込等)時は旧構成を無傷で維持し、稼働を継続する。
    private void ApplyReload(RadirecConfig newConfig)
    {
        IngestService newIngest;
        IDisposable? newResources;
        try
        {
            (newIngest, newResources) = CompositionRoot.Build(newConfig, dryRun: false, notify: true);
        }
        catch (Exception ex)
        {
            // 旧構成(グラフ + watcher + タイマー)は無傷。reload だけ失敗し、サービスは死なせない。
            _log.LogError(ex, "設定リロード: 新グラフの構築に失敗しました。旧構成を維持して継続します");
            return;
        }

        // 新グラフ構築成功 → 旧 config 由来ランタイムを破棄し、watcher/タイマーも新 config で張り直す。
        TeardownConfigRuntime();
        _ingest = newIngest;
        _resources = newResources;
        _config = newConfig;
        StartWatcher();       // 新 RadioRoot で監視。status.RadioRoot / Watching も更新する。
        StartPeriodicTimer(); // 新 ScanIntervalSeconds で定期タイマーを張り直す。

        // 新構成での走査は、この reload を起こしたトリガ(wake or coalesce された既存トリガ)を
        // 直列ループが本反復でそのまま ScanAsync に流すため、ここで追加要求はしない(二重走査を避ける)。
        _log.LogInformation("設定リロード完了。監視ルート: {Root}", _config.RadioRoot);
    }

    private void StartWatcher()
    {
        if (!Directory.Exists(_config.RadioRoot))
        {
            _log.LogWarning("監視ルートが存在しません: {Root}(定期スキャンのみで継続)", _config.RadioRoot);
            _status = _status with { RadioRoot = _config.RadioRoot, Watching = false };
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
        _status = _status with { RadioRoot = _config.RadioRoot, Watching = true };
    }

    private void StartPeriodicTimer()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, _config.ScanIntervalSeconds));
        _periodicTimer = new Timer(_ => Request("定期"), null, interval, interval);
    }

    // config 由来ランタイム(watcher + タイマー + 台帳/HTTP 資源)を破棄する。直列ループとトリガチャネルは
    // 生かしたまま(reload をまたいでコンシューマは生存する)。
    private void TeardownConfigRuntime()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _periodicTimer?.Dispose();
        _periodicTimer = null;
        _resources?.Dispose();
        _resources = null;
    }

    // イベントごとに単一タイマーを再スタート = 最後のイベントから静穏 WatchDebounceSeconds 秒後に発火。
    // reload/停止の teardown(直列ループ側)がこの threadpool コールバックと並行に _debounceTimer を
    // dispose→null しうるが安全: null なら no-op、破棄済みタイマーへの Timer.Change は .NET では例外を
    // 投げず false を返す(実測確認)。取りこぼしは新 watcher / 定期スキャンで回収する。
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

    private int _disposed;

    public override void Dispose()
    {
        // 同一インスタンスを DI で複数スロット(HostedService / IServiceControl)に登録しているため
        // コンテナが Dispose を複数回呼ぶ。二重解放を避け冪等にする。
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // コンシューマループ(ExecuteAsync)は StopAsync で完了済み。ここでは現行の config 由来資源を解放。
        TeardownConfigRuntime();
        base.Dispose();
    }
}
