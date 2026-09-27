using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Radirec.Core.Configuration;
using Radirec.Core.Ingestion;
using Radirec.Core.Reapply;
using Radirec.Gui.Core;
using Radirec.Hosting;
using Radirec.Hosting.Control;
// WinForms プロジェクトでは System.Windows.Forms.Timer と衝突するため、ワーカーが使う
// スレッドタイマーを明示エイリアスで固定する。
using Timer = System.Threading.Timer;

namespace Radirec.Gui;

/// <summary>
/// I: を監視し録音を検知即取り込みする常駐ワーカー。正しさはキャッチアップ + import-once 冪等で
/// 担保する(ADR-0003)。トリガ①FileSystemWatcher ②スリープ復帰(電源イベント)③起動時 ④定期
/// ⑤手動(トレイメニュー)⑥リロードはすべて単一の要求チャネルへ投げ、ExecuteAsync 内の**単一直列
/// コンシューマ**が順に処理する。これにより fire-and-forget を排し、停止時に在中スキャンをまたいで
/// リソースを破棄するレースを避ける。in-process 制御(ADR-0012)の <see cref="IServiceControl"/> 実装も
/// 兼ね、rescan は要求チャネルへ、status はスレッドセーフな状態スナップショットを直接返し、reload は
/// **config 由来ランタイム一式(グラフ + watcher + タイマー)をプロセス内で丸ごと再構築**する
/// (部分 hot-swap はしない)。統合設定の再適用(#126 / ADR-0016)も同じ直列ループの上で、reload の反映の後・
/// スキャンの前に行う。CLI から頼まれた走査の実行(#180 / ADR-0020)も同じループの上で、再適用の後・トリガの走査の前に行う。
///
/// トレイ常駐移行(ADR-0012)で、取込通知は EventLog シグナルではなく**注入された in-process publisher**
/// (トレイのトースト)で発行する。ホストは <see cref="Radirec.Gui"/> のユーザーセッションプロセス。
/// </summary>
public sealed class RadioMonitorWorker : BackgroundService, IServiceControl
{
    private readonly ILogger<RadioMonitorWorker> _log;
    private readonly string _configPath;

    // 注入された取込要約の publisher を包んだもの。取り込みグラフにはこれを渡し、走査の実行(#180)の間だけ新規取込のトーストを止める。
    private readonly ScanRunSummaryGate _summaryGate;
    private readonly IIngestFailurePublisher _failurePublisher;

    // 走査の実行(#180)。結果を返す非冪等の要求なので、再適用と同じく取りこぼさないキューで運ぶ(起こしはトリガチャネル)。
    private readonly ScanRunQueue _scanRuns;

    // config 由来のランタイム。reload で丸ごと差し替わるため readonly ではない(書き手は直列ループのみ)。
    private RadirecConfig _config;
    private IngestService _ingest;
    private IDisposable? _resources;

    // in-process 制御の status が読む状態。immutable record を差し替える(単一書き手 = ExecuteAsync
    // スレッド、読み手 = UI スレッド)。ADR-0010/0012。
    private volatile ServiceStatusSnapshot _status;

    // 直列コンシューマループの生死(#197)。起動〜稼働中は true(ループ開始前の reload もチャネルにバッファされ処理される)、
    // ループ終了(停止・起動処理の失敗)後に false。停止後の reload を誤受理しない・状態取得で「稼働中」と偽らないため。
    private readonly SerialLoop _loop = new();

    // 容量1・満杯時は書き捨て = 複数トリガを1回のスキャンに束ねる(コアレス)。
    private readonly Channel<string> _requests = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    // reload は冪等でないため上の DropWrite チャネルには乗せず、取りこぼさないスロットで運ぶ(ADR-0010)。
    // 起こしはトリガチャネルへの書き込み。set 後に必ずループが 1 回起きて Take するので取りこぼさない。
    private readonly PendingReloadSlot<RadirecConfig> _pendingReload;

    // 並行 reload の「読込→Set」を直列化する門(#43)。適用順序 = 受理順序を保つ。
    private readonly object _reloadGate = new();

    // 統合設定の再適用の実行(#126)。結果を返す非冪等の要求なので DropWrite のトリガチャネルには乗せず、
    // 取りこぼさないキューで運ぶ(起こしはトリガチャネル。reload のスロットと同じ理屈)。ループが抜けたら
    // 残りを失敗で完了させ、待っているトレイを起こす。
    private readonly ConcurrentQueue<ReapplyJob> _reapplyJobs = new();

    // 直近の設定リロードの反映に失敗した理由(成功なら null)。書き手・読み手とも直列ループ。
    // TryLoad は radirec.json しか見ないので、programs.json / stations.json の読み込み失敗はここで初めて判る。
    private string? _lastReloadError;

    // 再適用の確認・実行のジョブの中身(管理画面・トレイの「直前の確認」もここが持つ)。直列ループの上でだけ使う。
    private readonly ReapplyJobs _reapply;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer; // 書き込み中の連続イベントを束ねる(単一の再利用タイマー)
    private Timer? _periodicTimer; // 定期(安全網)

    public RadioMonitorWorker(
        RadirecConfig config,
        ServiceRuntimeOptions options,
        IIngestSummaryPublisher summaryPublisher,
        IIngestFailurePublisher failurePublisher,
        ILogger<RadioMonitorWorker> log)
    {
        _config = config;
        _configPath = options.ConfigPath;
        _summaryGate = new ScanRunSummaryGate(summaryPublisher);
        _scanRuns = new ScanRunQueue(_summaryGate);
        _failurePublisher = failurePublisher;
        _log = log;
        _pendingReload = new PendingReloadSlot<RadirecConfig>(() => Request("リロード"));
        _reapply = new ReapplyJobs(new ReapplyHost(this));
        // 取込通知は注入された in-process publisher で発行する(トレイのトースト。ADR-0012)。
        (_ingest, _resources) = BuildGraph(config);
        _status = new ServiceStatusSnapshot
        {
            Version = AppVersion.Of(typeof(RadioMonitorWorker).Assembly),
            StartedUtc = TimeProvider.System.GetUtcNow(),
            RadioRoot = config.RadioRoot,
            Watching = false, // StartWatcher 後に更新する
            StationsFile = config.StationsFile,
            ProgramsFile = config.ProgramsFile,
            LedgerPath = config.LedgerPath,
        };
    }

    // 注入された in-process publisher を渡して取り込みグラフを組む(ADR-0012)。成功要約は、走査の実行(#180)の間だけ止める門を通す。
    private (IngestService, IDisposable?) BuildGraph(RadirecConfig config) =>
        CompositionRoot.Build(
            config, dryRun: false, notify: true,
            summaryPublisher: _summaryGate, failurePublisher: _failurePublisher);

    // --- IServiceControl(in-process 制御。ADR-0010/0012)------------------------------------

    /// <summary>手動再スキャン。既存の要求チャネルへ coalesce される冪等操作(受理まで)。</summary>
    public void RequestRescan() => Request("手動");

    /// <summary>走査の実行を、最後のスキャンのきっかけ(status・管理画面のホーム)に出すときの名前。ループを起こすトリガにも使う。</summary>
    public const string ScanRunTrigger = "CLI";

    // 走査の実行の起こしだけが残っていたとき(キューは前の反復で処理済み)の通常の走査のきっかけ。束ねられて捨てられたトリガの分。
    private const string CoalescedTrigger = "再走査";

    /// <summary>
    /// 走査の実行(#180 / ADR-0020)。キューに積んでループを起こし、走査が終わると完了する結果を返す。走査は直列ループの上で、
    /// 再適用の後に走る(<see cref="ExecuteAsync"/>)。
    /// </summary>
    public Task<ScanRunResult> RunScanAsync()
    {
        if (!_loop.Running)
            return Task.FromResult(ScanRunResult.Failed(ScanRunFailure.Stopped, "取り込みが停止中のため受理できません"));

        var result = _scanRuns.Enqueue();
        Request(ScanRunTrigger);

        // 積んだ直後にループが抜けていた(停止と競合)なら、誰も処理しないので自分で中止にする。
        if (!_loop.Running)
            _scanRuns.FailPending();

        return result;
    }

    /// <summary>
    /// ライブ状態のスナップショット(在中スキャン中でも直列チャネルを介さず即応)。
    /// 取り込みループの生死(<see cref="SerialLoop.Running"/>)を毎回反映する。トレイは生存したまま
    /// ワーカーだけ異常終了した場合に「稼働中」と偽らないため(ADR-0012)。起動処理の失敗も、異常終了と見分けられるように載せる(#217)。
    /// </summary>
    public ServiceStatusSnapshot GetStatus() =>
        _status with { Running = _loop.Running, StartFailure = _loop.StartFailure?.Message };

    /// <summary>
    /// 設定リロード。新 config の読込+構文検証だけ**同期実行**(グラフに触れない)し、妥当なら再構築を
    /// スロットへ投入して即「受理」、不正なら旧構成を維持して失敗を返す。実グラフ差し替えは在中スキャン
    /// 完了後に直列ループ上で <see cref="ApplyReload"/> が行う(ADR-0010)。
    /// </summary>
    public ReloadResult RequestReload()
    {
        // 停止処理中は直列ループが要求を Take しない(誤って「受理」を返さない)。
        if (!_loop.Running)
            return ReloadResult.Failed("取り込みが停止中のため受理できません");

        // 制御は並行に捌かれる(#43)ので reload も同時に来うる。読込→Set を lock で直列化し、
        // 「最後に受理した reload の config が適用される」順序保証を保つ(読込と Set の間に別 reload が
        // 割り込んで古い内容が最終値になるドリフトを防ぐ)。読込は速く lock 競合は実害にならない。
        lock (_reloadGate)
        {
            if (!ConfigLoading.TryLoad(_configPath, out var newConfig, out var error))
                return ReloadResult.Failed(error!);

            _pendingReload.Set(newConfig!);
            return ReloadResult.Ok();
        }
    }

    /// <summary>
    /// 統合設定の再適用の確認(#126)。<see cref="RequestReload"/> と同じ検証で設定リロードを投入し、続けて
    /// dry-run のジョブを投入する。ループはジョブを取り出した直後にも保留中の reload を反映してから処理するので、
    /// dry-run は必ず新しい設定で走り、反映に失敗していれば中止する(<see cref="ReapplyJobs"/>)。
    /// </summary>
    public Task<ReapplyResult> PlanReapplyAsync()
    {
        if (!_loop.Running)
            return Task.FromResult(ReapplyResult.Stopped(StoppedMessage));

        lock (_reloadGate)
        {
            // error は前置きを含む(ConfigLoading.TryLoad)。RequestReload の拒否と同じくそのまま返す(#218)。
            if (!ConfigLoading.TryLoad(_configPath, out var newConfig, out var error))
                return Task.FromResult(ReapplyResult.Failed(error!));

            // reload を先に置く(Set)。ジョブはその後に積むので、ループがジョブを取り出した時点で reload も
            // 見えており、ジョブの処理の直前の Take で反映される(ExecuteAsync)。
            _pendingReload.Set(newConfig!);
            return EnqueueReapply(new ReapplyJob(ReapplyJobKind.Plan));
        }
    }

    /// <summary>
    /// 保存前の番組の設定での確認(管理画面の番組ページ。#154)。設定リロードはせず、今の設定で dry-run のジョブを投入する。
    /// </summary>
    public Task<ReapplyResult> PlanReapplyAsync(ProgramMasterConfig programs)
    {
        if (!_loop.Running)
            return Task.FromResult(ReapplyResult.Stopped(StoppedMessage));

        return EnqueueReapply(new ReapplyJob(ReapplyJobKind.PlanDraft, Draft: programs));
    }

    /// <summary>確認した再適用の実行(#126)。直列ループの上で、確認のときと設定が同じ場合だけ実行する。</summary>
    public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan)
    {
        if (!_loop.Running)
            return Task.FromResult(ReapplyResult.Stopped(StoppedMessage));

        return EnqueueReapply(new ReapplyJob(ReapplyJobKind.Apply, Plan: plan));
    }

    /// <summary>
    /// 保存した番組の設定での「設定リロード → 再適用の実行」(管理画面の「保存して適用」。#154・ADR-0016)。
    /// 直列ループの 1 つのジョブの中で続けて行う(<see cref="ReapplyJobKind.ReloadAndApply"/>)。
    /// </summary>
    public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan)
    {
        if (!_loop.Running)
            return Task.FromResult(ReapplyResult.Stopped(StoppedMessage));

        return EnqueueReapply(new ReapplyJob(ReapplyJobKind.ReloadAndApply, Plan: plan));
    }

    /// <summary>
    /// CLI の再適用の確認(#181・ADR-0020)。設定リロードはせず、今の設定とディスクの番組・放送局の設定で dry-run し、予定 ID を付けて返す。
    /// 管理画面・トレイの直前の確認には触れない(<see cref="ReapplyJobKind.PlanForCli"/>)。
    /// </summary>
    public Task<ReapplyResult> PlanReapplyForCliAsync()
    {
        if (!_loop.Running)
            return Task.FromResult(ReapplyResult.Stopped(StoppedMessage));

        return EnqueueReapply(new ReapplyJob(ReapplyJobKind.PlanForCli));
    }

    /// <summary>
    /// CLI の再適用の実行(#181・ADR-0020)。設定リロード → dry-run し直し → 予定 ID が一致したときだけ実行する
    /// (<see cref="ReapplyJobKind.ApplyPlanId"/>)。管理画面・トレイの直前の確認には触れない。
    /// </summary>
    public Task<ReapplyResult> ApplyReapplyPlanAsync(string planId)
    {
        if (!_loop.Running)
            return Task.FromResult(ReapplyResult.Stopped(StoppedMessage));

        return EnqueueReapply(new ReapplyJob(ReapplyJobKind.ApplyPlanId, PlanId: planId));
    }

    private const string StoppedMessage = "取り込みが停止中のため受理できません";

    private Task<ReapplyResult> EnqueueReapply(ReapplyJob job)
    {
        _reapplyJobs.Enqueue(job);
        Request("再適用");

        // 積んだ直後にループが抜けていた(停止と競合)なら、誰も処理しないので自分で失敗にする。
        if (!_loop.Running)
            FailPendingReapplyJobs();

        return job.Completion.Task;
    }

    // ループが抜けた後に残ったジョブを失敗で完了させる(トレイが永久に待たないように)。
    private void FailPendingReapplyJobs()
    {
        while (_reapplyJobs.TryDequeue(out var job))
            job.Completion.TrySetResult(ReapplyResult.Stopped("取り込みが停止したため中止しました"));
    }

    // 起動処理(掃除・監視・タイマー)もループと同じ SerialLoop の中で走らせ、そこで例外が出ても(NAS 未接続など)「停止」に反映して
    // 以降の要求を受理しない(#197)。.NET 10 から ExecuteAsync は全体が別のタスクで走り、起動処理の例外はホストの起動
    // (Program.cs の host.StartAsync())には伝わらない。なのでここで gui.log に残し、状態にも起動処理の失敗として出す(#217)。
    // 投げ直した例外は BackgroundServiceExceptionBehavior.StopHost でホストを止める(.NET の [Host] の行も続けて出る)。
    // 停止要求ではないのにループが抜けたとき(#204)も gui.log に残す(トレイは生きたまま取り込みだけが黙って止まらないように)。
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _loop.RunAsync(
            StartLoop,
            ConsumeAsync,
            onStopped: () =>
            {
                FailPendingReapplyJobs();
                _scanRuns.FailPending();
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            },
            onUnexpectedExit: ex => _log.LogWarning(ex, "停止要求ではないのに取り込みのループが終わりました。取り込みは停止しています"),
            onStartFailed: ex => _log.LogError(ex, "取り込みの起動処理に失敗しました。取り込みは停止しています(原因を直してから再起動してください)"),
            stoppingToken);

    private void StartLoop()
    {
        _log.LogInformation("Radirec 常駐取り込みの起動処理を始めます。監視ルート: {Root}", _config.RadioRoot);

        var orphans = _ingest.CleanupOrphans();
        if (orphans > 0)
            _log.LogInformation("中断で残った .partial を {Count} 件掃除しました", orphans);

        StartWatcher();
        StartPeriodicTimer();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Request("起動時");
        _log.LogInformation("Radirec 常駐取り込みを開始しました");
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        // 単一直列コンシューマ。停止時はこのループが在中スキャン完了後に抜ける(StopAsync が待つ)。
        await foreach (var trigger in _requests.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            // 在中スキャン完了後(= 反復の先頭)にのみ reload を適用する。ADR-0010 の直列不変条件を保つ。
            if (_pendingReload.Take() is { } newConfig)
                ApplyReload(newConfig);

            // 再適用(#126)は reload の反映の後、スキャンの前に処理する。取り込みと同時には走らない。
            // トレイは別スレッドで「reload の Set → ジョブの Enqueue」を行うので、上の Take の後・ここの
            // Dequeue の前に割り込まれうる。ジョブを取り出した後にもう一度 Take し、ジョブより先に置かれた
            // reload を必ず反映してから処理する(Dequeue でジョブが見えたなら、その前の Set も見える)。
            while (_reapplyJobs.TryDequeue(out var job))
            {
                if (_pendingReload.Take() is { } configBeforeJob)
                    ApplyReload(configBeforeJob);

                await RunReapplyAsync(job, stoppingToken);
            }

            // 走査の実行(#180)。積まれた分をまとめて 1 回の走査で処理する(新規取込のトーストは出さない)。
            // 再適用と同じく、取り出した**後**に保留中の reload を反映する(頼む側は「reload の Set → 走査の実行の Enqueue」の
            // 順に行うので、取り出せた走査の実行より前に置かれた reload は必ず見える。radirec reload → radirec ingest 等)。
            if (_scanRuns.TakePending() is { } scanRuns)
            {
                if (_pendingReload.Take() is { } configBeforeScanRun)
                    ApplyReload(configBeforeScanRun);
                await _scanRuns.RunAsync(scanRuns, ct => RunScanAsync(ScanRunTrigger, ct), stoppingToken);

                // この反復で走査の実行を行ったなら、トリガが何であれ通常の走査はもう走らせない(トリガはどれも走査の実行の
                // 走査より前に来ているので、その走査が覆っている。容量 1 のチャネルで捨てられたトリガも同じ)。
                continue;
            }

            // 走査の実行の起こしだけが残っていてキューが空(前の反復が先に処理した)なら、その間に捨てられたトリガがありうるので、
            // 通常の走査として 1 回走らせる。
            await ScanAsync(trigger == ScanRunTrigger ? CoalescedTrigger : trigger, stoppingToken);
        }
    }

    // 再適用のジョブを 1 つ処理し、必ず結果で完了させる。ここから例外を漏らすと BackgroundService が fault して
    // 常駐ごと落ちるので、停止要求以外はすべて結果にして返す(停止要求でない取り消しも失敗の結果にする。#204)。
    private Task RunReapplyAsync(ReapplyJob job, CancellationToken cancellationToken) =>
        SerialLoop.RunStepAsync(
            async ct => job.Completion.TrySetResult(await _reapply.RunAsync(job, ct)),
            onFailure: ex =>
            {
                _log.LogError(ex, "統合設定の再適用で想定外の例外");
                job.Completion.TrySetResult(ReapplyResult.Failed($"再適用に失敗しました: {ex.Message}"));
            },
            cancellationToken,
            onStopRequested: () => job.Completion.TrySetResult(ReapplyResult.Stopped("取り込みが停止したため中止しました")));

    // 読み込み済みの設定で再適用を組み立てて走らせる。組み立て(台帳オープン等)の失敗は結果で返す。
    // 台帳のある場所(NAS など)に届かないときは、それと分かる印を付ける(CLI は 5 にする。#223)。文言はどちらも同じ。
    private async Task<(ReapplyReport? Report, ReapplyResult? Failure)> RunServiceAsync(
        RadirecConfig config, ReapplySettings settings, bool dryRun, CancellationToken cancellationToken)
    {
        ReapplyService service;
        IDisposable? disposable;
        try
        {
            (service, disposable) = CompositionRoot.BuildReapply(config, dryRun, settings);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "統合設定の再適用: 組み立てに失敗しました");
            return (null, ReapplyResult.BuildFailed(ex));
        }

        using (disposable)
            return (await service.RunAsync(dryRun, cancellationToken), null);
    }

    // 1 回ずつの明細は gui.log に書く(ダイアログはアルバム単位のまとめだけ)。
    private void LogReport(ReapplyReport report)
    {
        foreach (var line in ReapplyDialogFormatter.BuildLogLines(report))
            _log.LogInformation("{Line}", line);
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
            (newIngest, newResources) = BuildGraph(newConfig);
        }
        catch (Exception ex)
        {
            // 旧構成(グラフ + watcher + タイマー)は無傷。reload だけ失敗し、常駐は死なせない。
            _log.LogError(ex, "設定リロード: 新グラフの構築に失敗しました。旧構成を維持して継続します");
            _lastReloadError = ex.Message; // 再適用(#126)はこれを見て中止する
            return;
        }

        _lastReloadError = null;

        // 新グラフ構築成功 → 旧 config 由来ランタイムを破棄し、watcher/タイマーも新 config で張り直す。
        // この swap 相は直列ループスレッド上。ここで throw を漏らすと BackgroundService が fault し、
        // 既定の StopHost で常駐全体が落ちる(旧構成 teardown 済み)ため、必ず捕捉して継続する。
        // StartWatcher は非 throw 化済みだが、想定外の例外でも常駐を死なせない最後の砦。
        try
        {
            TeardownConfigRuntime();
            _ingest = newIngest;
            _resources = newResources;
            _config = newConfig;
            // 設定ページの「ファイルを開く」(#143 / #152)が開く場所と、管理画面が読む台帳(#196)も、差し替えた設定に合わせる。
            _status = _status with
            {
                StationsFile = newConfig.StationsFile,
                ProgramsFile = newConfig.ProgramsFile,
                LedgerPath = newConfig.LedgerPath,
            };
            StartWatcher();      // 新 RadioRoot で監視。status.RadioRoot / Watching も更新する。
            StartPeriodicTimer(); // 新 ScanIntervalSeconds で定期タイマーを張り直す。
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "設定リロードの適用中に想定外の例外。定期スキャンで継続を試みます");
            // 新しい config に差し替わる前に落ちたなら、再適用(#126)も新しい設定では走らせない。
            if (!ReferenceEquals(_config, newConfig))
                _lastReloadError = ex.Message;
            // 自動走査を絶やさないよう、最低限の定期タイマーを確保する(監視は次回起動 or 再 reload で回収)。
            if (_periodicTimer is null)
            {
                try { StartPeriodicTimer(); }
                catch (Exception ex2) { _log.LogError(ex2, "定期タイマーの再確保にも失敗しました"); }
            }
            return;
        }

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

        // FileSystemWatcher の生成は Directory.Exists を通っても失敗しうる(長すぎるパス・TOCTOU で
        // 直後に消えた等)。ここで throw すると reload の swap 相を巻き込み常駐を落とすため、
        // 失敗は定期スキャンのみへの degrade として扱い、throw しない(watcher は次回起動/再 reload で回収)。
        try
        {
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
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FileSystemWatcher の生成に失敗しました: {Root}(定期スキャンのみで継続)", _config.RadioRoot);
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _watcher?.Dispose();
            _watcher = null;
            _status = _status with { RadioRoot = _config.RadioRoot, Watching = false };
        }
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

    // トリガの走査。例外は gui.log に残して握る(常駐を落とさない)。停止要求だけはループへ伝える。
    // 停止要求だけはループへ伝播させて終了する。それ以外(停止要求でない取り消しを含む。#204)は gui.log に残し、次のトリガで走査する。
    private Task ScanAsync(string trigger, CancellationToken cancellationToken) =>
        SerialLoop.RunStepAsync(
            ct => RunScanAsync(trigger, ct),
            onFailure: ex => _log.LogError(ex, "{Trigger}スキャンで想定外の例外", trigger),
            cancellationToken);

    // 走査を 1 回行い、結果を gui.log と status に残して返す。例外は呼び手へ投げる(走査の実行では、結果にして CLI へ返す)。
    private async Task<IngestReport> RunScanAsync(string trigger, CancellationToken cancellationToken)
    {
        try
        {
            var report = await _ingest.RunOnceAsync(dryRun: false, cancellationToken);
            var imported = report.Count(IngestOutcome.Imported);
            var failed = report.Count(IngestOutcome.Failed);
            if (imported > 0 || failed > 0)
            {
                var refresh = report.LibraryRefresh switch
                {
                    LibraryRefreshStatus.Started => "起動",
                    LibraryRefreshStatus.NotConfigured => "未設定",
                    LibraryRefreshStatus.Failed or LibraryRefreshStatus.Misconfigured => report.RefreshError ?? "失敗",
                    _ => "-",
                };
                // 止めたとき(走査の実行・設定でオフ)は「発行」と書かず理由を残す。出していない通知を「発行」と
                // 書くと、トーストが出ない理由を調べる手がかりを誤らせる(#215)。
                var notify = report.NotificationPublished ? "発行"
                    : report.NotificationSuppressedReason is { } suppressed ? $"止めた({suppressed})"
                    : report.NotificationError ?? "-";
                // 失敗通知の発行結果も残す。新規失敗が無ければ "-"、発行失敗なら理由を残して
                // 「失敗トーストが出ない理由」を診断可能にする(さもないと無痕跡)。
                var failNotify = report.FailureNotificationPublished ? "発行" : report.FailureNotificationError ?? "-";
                _log.LogInformation(
                    "{Trigger}スキャン: 取込 {Imported} / 失敗 {Failed} / Jellyfin更新 {Refresh} / 通知 {Notify} / 失敗通知 {FailNotify}",
                    trigger, imported, failed, refresh, notify, failNotify);
            }

            LogRenamedImports(report.Items);

            // 走査ごとに status スナップショットを更新(in-process 制御)。0 件走査でも
            // 「最後にいつ走ったか」を反映する。書き手はこの直列コンシューマ 1 つだけ。
            _status = _status with
            {
                LastScanCompletedUtc = TimeProvider.System.GetUtcNow(),
                LastScanTrigger = trigger,
                LastImported = imported,
                LastFailed = failed,
            };
            return report;
        }
        catch (IngestCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // 停止要求で打ち切った走査。結果の行は最後まで終わったときにしか書かないので、それまでに取り込んだ分を 1 行残す
            // (残さないと、台帳と出力先を見ないと分からない。#218)。停止要求はそのまま呼び手へ伝える。
            _log.LogInformation(
                "{Trigger}スキャン(停止のため中断): 取込 {Imported} / 失敗 {Failed}",
                trigger, ex.Count(IngestOutcome.Imported), ex.Count(IngestOutcome.Failed));
            LogRenamedImports(ex.Items); // 打ち切るまでに別名で置いた取込も、最後まで終わった走査と同じく残す
            throw;
        }
        catch (Exception ex) when (
            trigger == ScanRunTrigger && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // 走査の実行の失敗は結果にして CLI へ返すが、トリガの走査(ScanAsync)と同じく gui.log にも残す。停止要求だけは残さない
            // (停止要求でない OperationCanceledException は ScanRunQueue が内部の失敗にするので、ここで残す)。
            _log.LogError(ex, "{Trigger}スキャンで想定外の例外", trigger);
            throw;
        }
    }

    // 出力先の同名衝突を別名で避けた取込は Warning で残す。同日・別時刻の正当なケースでも起きるので
    // トーストにはせず、near-dup を疑うときに gui.log から辿れるようにする(ADR-0015)。
    private void LogRenamedImports(IEnumerable<IngestItemResult> items)
    {
        foreach (var item in items.Where(i => i.Outcome == IngestOutcome.Imported && i.Message is not null))
            _log.LogWarning("{Message}: {Output}", item.Message, item.OutputPath);
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

    // 再適用のジョブ(ReapplyJobs)に、設定の差し替え・再適用の組み立て・ログを貸す。どれも直列ループの上から呼ばれる。
    private sealed class ReapplyHost(RadioMonitorWorker worker) : IReapplyJobHost
    {
        public RadirecConfig Config => worker._config;

        public string? LastReloadError => worker._lastReloadError;

        public bool TryLoadConfig(out RadirecConfig? config, out string? error) =>
            ConfigLoading.TryLoad(worker._configPath, out config, out error);

        public void ApplyReload(RadirecConfig config) => worker.ApplyReload(config);

        public ReapplySettings LoadSettings(RadirecConfig config) => CompositionRoot.LoadReapplySettings(config);

        public StationMasterConfig LoadStations(RadirecConfig config) =>
            ConfigLoader.LoadOptional<StationMasterConfig>(config.StationsFile);

        public Task<(ReapplyReport? Report, ReapplyResult? Failure)> RunServiceAsync(
            RadirecConfig config, ReapplySettings settings, bool dryRun, CancellationToken cancellationToken) =>
            worker.RunServiceAsync(config, settings, dryRun, cancellationToken);

        public void LogReport(ReapplyReport report) => worker.LogReport(report);

        public void LogWarning(string message) => worker._log.LogWarning("{Message}", message);

        public void LogError(Exception exception, string message) => worker._log.LogError(exception, "{Message}", message);
    }
}
