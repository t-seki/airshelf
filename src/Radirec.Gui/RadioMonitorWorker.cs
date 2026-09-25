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
/// スキャンの前に行う。
///
/// トレイ常駐移行(ADR-0012)で、取込通知は EventLog シグナルではなく**注入された in-process publisher**
/// (トレイのトースト)で発行する。ホストは <see cref="Radirec.Gui"/> のユーザーセッションプロセス。
/// </summary>
public sealed class RadioMonitorWorker : BackgroundService, IServiceControl
{
    private readonly ILogger<RadioMonitorWorker> _log;
    private readonly string _configPath;
    private readonly IIngestSummaryPublisher _summaryPublisher;
    private readonly IIngestFailurePublisher _failurePublisher;

    // config 由来のランタイム。reload で丸ごと差し替わるため readonly ではない(書き手は直列ループのみ)。
    private RadirecConfig _config;
    private IngestService _ingest;
    private IDisposable? _resources;

    // in-process 制御の status が読む状態。immutable record を差し替える(単一書き手 = ExecuteAsync
    // スレッド、読み手 = UI スレッド)。ADR-0010/0012。
    private volatile ServiceStatusSnapshot _status;

    // 直列コンシューマループが reload を処理できる状態か。起動〜稼働中は true(ループ開始前の reload も
    // チャネルにバッファされ処理される)、ループ終了(停止)後に false。停止後の reload を誤受理しないため。
    private volatile bool _running = true;

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

    // 直近の再適用の確認(dry-run)と、そのときの config・番組/放送局の設定。実行はこの確認に対してだけ行い、
    // 番組/放送局の設定も読み直さずにこれを使う(直列ループのみが読み書きする)。
    private (ReapplyReport Plan, RadirecConfig Config, ReapplySettings Settings)? _lastReapplyPlan;

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
        _summaryPublisher = summaryPublisher;
        _failurePublisher = failurePublisher;
        _log = log;
        _pendingReload = new PendingReloadSlot<RadirecConfig>(() => Request("リロード"));
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
        };
    }

    // 注入された in-process publisher を渡して取り込みグラフを組む(ADR-0012)。
    private (IngestService, IDisposable?) BuildGraph(RadirecConfig config) =>
        CompositionRoot.Build(
            config, dryRun: false, notify: true,
            summaryPublisher: _summaryPublisher, failurePublisher: _failurePublisher);

    // --- IServiceControl(in-process 制御。ADR-0010/0012)------------------------------------

    /// <summary>手動再スキャン。既存の要求チャネルへ coalesce される冪等操作(受理まで)。</summary>
    public void RequestRescan() => Request("手動");

    /// <summary>
    /// ライブ状態のスナップショット(在中スキャン中でも直列チャネルを介さず即応)。
    /// 取り込みループの生死(<see cref="_running"/>)を毎回反映する。トレイは生存したまま
    /// ワーカーだけ異常終了した場合に「稼働中」と偽らないため(ADR-0012)。
    /// </summary>
    public ServiceStatusSnapshot GetStatus() => _status with { Running = _running };

    /// <summary>
    /// 設定リロード。新 config の読込+構文検証だけ**同期実行**(グラフに触れない)し、妥当なら再構築を
    /// スロットへ投入して即「受理」、不正なら旧構成を維持して失敗を返す。実グラフ差し替えは在中スキャン
    /// 完了後に直列ループ上で <see cref="ApplyReload"/> が行う(ADR-0010)。
    /// </summary>
    public ReloadResult RequestReload()
    {
        // 停止処理中は直列ループが要求を Take しない(誤って「受理」を返さない)。
        if (!_running)
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
    /// dry-run は必ず新しい設定で走り、反映に失敗していれば中止する(<see cref="ReapplyAsync"/>)。
    /// </summary>
    public Task<ReapplyResult> PlanReapplyAsync()
    {
        if (!_running)
            return Task.FromResult(ReapplyResult.Failed("取り込みが停止中のため受理できません"));

        lock (_reloadGate)
        {
            if (!ConfigLoading.TryLoad(_configPath, out var newConfig, out var error))
                return Task.FromResult(ReapplyResult.Failed($"設定の読み込みに失敗しました: {error}"));

            // reload を先に置く(Set)。ジョブはその後に積むので、ループがジョブを取り出した時点で reload も
            // 見えており、ジョブの処理の直前の Take で反映される(ExecuteAsync)。
            _pendingReload.Set(newConfig!);
            return EnqueueReapply(new ReapplyJob(Plan: null, Draft: null));
        }
    }

    /// <summary>
    /// 保存前の番組の設定での確認(管理画面の番組ページ。#154)。設定リロードはせず、今の設定で dry-run のジョブを投入する。
    /// </summary>
    public Task<ReapplyResult> PlanReapplyAsync(ProgramMasterConfig programs)
    {
        if (!_running)
            return Task.FromResult(ReapplyResult.Failed("取り込みが停止中のため受理できません"));

        return EnqueueReapply(new ReapplyJob(Plan: null, Draft: programs));
    }

    /// <summary>確認した再適用の実行(#126)。直列ループの上で、確認のときと設定が同じ場合だけ実行する。</summary>
    public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan)
    {
        if (!_running)
            return Task.FromResult(ReapplyResult.Failed("取り込みが停止中のため受理できません"));

        return EnqueueReapply(new ReapplyJob(plan, Draft: null));
    }

    /// <summary>
    /// 保存した番組の設定での「設定リロード → 再適用の実行」(管理画面の「保存して適用」。#154・ADR-0016)。
    /// 直列ループの 1 つのジョブの中で続けて行う(<see cref="ReloadAndApplyAsync"/>)。
    /// </summary>
    public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan)
    {
        if (!_running)
            return Task.FromResult(ReapplyResult.Failed("取り込みが停止中のため受理できません"));

        return EnqueueReapply(new ReapplyJob(plan, Draft: null, ReloadFirst: true));
    }

    private Task<ReapplyResult> EnqueueReapply(ReapplyJob job)
    {
        _reapplyJobs.Enqueue(job);
        Request("再適用");

        // 積んだ直後にループが抜けていた(停止と競合)なら、誰も処理しないので自分で失敗にする。
        if (!_running)
            FailPendingReapplyJobs();

        return job.Completion.Task;
    }

    // ループが抜けた後に残ったジョブを失敗で完了させる(トレイが永久に待たないように)。
    private void FailPendingReapplyJobs()
    {
        while (_reapplyJobs.TryDequeue(out var job))
            job.Completion.TrySetResult(ReapplyResult.Failed("取り込みが停止したため中止しました"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Radirec 常駐取り込み開始。監視ルート: {Root}", _config.RadioRoot);

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

                await ScanAsync(trigger, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 停止要求。正常終了。
        }
        finally
        {
            _running = false; // 以降の reload 要求は受理しない(誤成功を返さない)。
            FailPendingReapplyJobs();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }

    // 再適用のジョブを 1 つ処理し、必ず結果で完了させる。ここから例外を漏らすと BackgroundService が fault して
    // 常駐ごと落ちるので、停止要求(OCE)以外はすべて結果にして返す。
    private async Task RunReapplyAsync(ReapplyJob job, CancellationToken cancellationToken)
    {
        try
        {
            job.Completion.TrySetResult(await ReapplyAsync(job, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            job.Completion.TrySetResult(ReapplyResult.Failed("取り込みが停止したため中止しました"));
            throw; // 停止要求はコンシューマループへ伝播させて終了する。
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "統合設定の再適用で想定外の例外");
            job.Completion.TrySetResult(ReapplyResult.Failed($"再適用に失敗しました: {ex.Message}"));
        }
    }

    // plan が null なら確認(dry-run)、あれば実行(ReloadFirst なら設定リロードから)。
    private Task<ReapplyResult> ReapplyAsync(ReapplyJob job, CancellationToken cancellationToken) => job switch
    {
        { Plan: { } plan, ReloadFirst: true } => ReloadAndApplyAsync(plan, cancellationToken),
        { Plan: { } plan } => ApplyAsync(plan, cancellationToken),
        _ => PlanAsync(job.Draft, cancellationToken),
    };

    // 確認:今の config(reload 反映後)で番組/放送局の設定を読み、dry-run する。読んだ設定は実行に持ち越す。
    // draft があれば(管理画面の番組ページ。#154)、番組の設定はディスクの programs.json ではなくそれを使う。
    private async Task<ReapplyResult> PlanAsync(ProgramMasterConfig? draft, CancellationToken cancellationToken)
    {
        _lastReapplyPlan = null;

        // 設定リロードが反映できなかった(programs.json の JSON が壊れている等)。取り込みは旧構成のまま
        // 稼働しているので、再適用だけを新しい設定で走らせることはせず中止する(ADR-0016)。
        // 下書きで確かめる(#154)ときは止めない。実行の段(ReloadAndApplyAsync)で必ずリロードを試し直し、失敗なら実行しない。
        if (ReapplyPlanGate.BlockingReloadError(hasDraft: draft is not null, _lastReloadError) is { } reloadError)
        {
            _log.LogWarning("統合設定の再適用: 設定の反映に失敗したため中止しました: {Error}", reloadError);
            return ReapplyResult.Failed($"設定の読み込みに失敗しました: {reloadError}");
        }

        var config = _config;
        ReapplySettings settings;
        try
        {
            settings = draft is null
                ? CompositionRoot.LoadReapplySettings(config)
                : new ReapplySettings(ConfigLoader.LoadOptional<StationMasterConfig>(config.StationsFile), draft);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "統合設定の再適用: 番組・放送局の設定の読み込みに失敗しました");
            return ReapplyResult.Failed($"設定の読み込みに失敗しました: {ex.Message}");
        }

        var (plan, error) = await RunServiceAsync(config, settings, dryRun: true, cancellationToken);
        if (plan is null)
            return ReapplyResult.Failed(error!);

        LogReport(plan);
        _lastReapplyPlan = (plan, config, settings);
        return ReapplyResult.Ok(plan);
    }

    // 実行:確認のときの config・番組/放送局の設定で、まず dry-run を計算し直し、確認した予定と同じときだけ実行する。
    // 確認ダイアログを開いている間に programs.json が編集された・取り込みでファイルや台帳が変わった等で予定が
    // ずれていれば、見せていない変更をしないよう中止する。
    private const string RetryReapply = "もう一度確認からやり直してください";

    private async Task<ReapplyResult> ApplyAsync(ReapplyReport plan, CancellationToken cancellationToken)
    {
        if (_lastReapplyPlan is not { } last || !ReferenceEquals(last.Plan, plan))
            return ReapplyResult.Stale($"確認した内容が古くなったため中止しました。{RetryReapply}");

        // 実行に使えるのは直前の確認だけ。ここで忘れる(同じ確認で 2 回実行しない・中止した確認を再利用しない)。
        _lastReapplyPlan = null;

        if (!ReferenceEquals(last.Config, _config))
            return ReapplyResult.Stale($"確認のあとに設定が変わったため中止しました。{RetryReapply}");

        return await ReplanAndRunAsync(plan, last.Config, last.Settings, cancellationToken);
    }

    // 保存した設定での実行(管理画面の「保存して適用」。#154・ADR-0016):設定リロード → 読み直した設定が確認のときと
    // 値で同じかを確かめる → dry-run し直して予定が同じなら実行。1 つのジョブの中で続けて行うので、リロードと再適用の間に
    // 取り込みの走査は挟まらない(再適用は新しい設定・取り込みは古い設定、という食い違いを作らない)。
    // リロードに失敗したら再適用はしない(取り込みは前の設定のまま)。どこで止まっても、もう一度確かめてから呼び直せる。
    private async Task<ReapplyResult> ReloadAndApplyAsync(ReapplyReport plan, CancellationToken cancellationToken)
    {
        if (_lastReapplyPlan is not { } last || !ReferenceEquals(last.Plan, plan))
            return ReapplyResult.Stale($"確認した内容が古くなったため中止しました。{RetryReapply}");
        _lastReapplyPlan = null;

        // 1. 設定リロード(RequestReload と同じ検証 → ApplyReload と同じ組み直し。ここは直列ループの上なので直接反映する)。
        const string KeepsRunning = "取り込みは前の設定のまま動いています";
        if (!ConfigLoading.TryLoad(_configPath, out var newConfig, out var loadError))
        {
            _log.LogWarning("統合設定の再適用: 設定の読み込みに失敗したため中止しました: {Error}", loadError);
            return ReapplyResult.Failed($"設定の読み込みに失敗しました: {loadError}({KeepsRunning})");
        }
        ApplyReload(newConfig!);
        if (_lastReloadError is { } reloadError || !ReferenceEquals(_config, newConfig))
        {
            var reason = _lastReloadError ?? "設定を差し替えられませんでした";
            _log.LogWarning("統合設定の再適用: 設定の反映に失敗したため中止しました: {Error}", reason);
            return ReapplyResult.Failed($"設定の反映に失敗しました: {reason}({KeepsRunning})");
        }

        // 2. 読み直した番組・放送局の設定が、確かめたとき(下書き)と値で同じか。
        var config = _config;
        ReapplySettings settings;
        try
        {
            settings = CompositionRoot.LoadReapplySettings(config);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "統合設定の再適用: 番組・放送局の設定の読み込みに失敗しました");
            return ReapplyResult.Failed($"設定の読み込みに失敗しました: {ex.Message}");
        }
        if (!settings.HasSameValuesAs(last.Settings))
        {
            _log.LogWarning("統合設定の再適用: 読み直した番組・放送局の設定が確認のときと違うため中止しました");
            return ReapplyResult.Stale($"確認のあとに設定が変わったため中止しました。{RetryReapply}");
        }

        // 3. 読み直した設定で dry-run し直し、予定が同じなら実行する。
        return await ReplanAndRunAsync(plan, config, settings, cancellationToken);
    }

    // 実行の直前に dry-run を計算し直し、確認した予定と同じ(値で)ときだけ実行する。
    private async Task<ReapplyResult> ReplanAndRunAsync(
        ReapplyReport plan, RadirecConfig config, ReapplySettings settings, CancellationToken cancellationToken)
    {
        var (replanned, replanError) = await RunServiceAsync(config, settings, dryRun: true, cancellationToken);
        if (replanned is null)
            return ReapplyResult.Failed(replanError!);
        if (!replanned.HasSamePlanAs(plan))
        {
            _log.LogWarning("統合設定の再適用: 確認のあとに状態が変わったため中止しました(実行直前の予定が確認と違います)");
            LogReport(replanned);
            return ReapplyResult.Stale($"確認のあとに状態が変わったため中止しました(取り込みや設定の変更で予定が変わりました)。{RetryReapply}");
        }

        var (result, error) = await RunServiceAsync(config, settings, dryRun: false, cancellationToken);
        if (result is null)
            return ReapplyResult.Failed(error!);

        LogReport(result);
        return ReapplyResult.Ok(result);
    }

    // 読み込み済みの設定で再適用を組み立てて走らせる。組み立て(台帳オープン等)の失敗は理由で返す。
    private async Task<(ReapplyReport? Report, string? Error)> RunServiceAsync(
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
            return (null, $"再適用の準備に失敗しました: {ex.Message}");
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
            // 設定ページの「ファイルを開く」(#143 / #152)が開く場所も、差し替えた設定に合わせる。
            _status = _status with { StationsFile = newConfig.StationsFile, ProgramsFile = newConfig.ProgramsFile };
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

    private async Task ScanAsync(string trigger, CancellationToken cancellationToken)
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
                    LibraryRefreshStatus.Failed => report.RefreshError ?? "失敗",
                    _ => "-",
                };
                var notify = report.NotificationPublished ? "発行" : report.NotificationError ?? "-";
                // 失敗通知の発行結果も残す。新規失敗が無ければ "-"、発行失敗なら理由を残して
                // 「失敗トーストが出ない理由」を診断可能にする(さもないと無痕跡)。
                var failNotify = report.FailureNotificationPublished ? "発行" : report.FailureNotificationError ?? "-";
                _log.LogInformation(
                    "{Trigger}スキャン: 取込 {Imported} / 失敗 {Failed} / Jellyfin更新 {Refresh} / 通知 {Notify} / 失敗通知 {FailNotify}",
                    trigger, imported, failed, refresh, notify, failNotify);
            }

            // 出力先の同名衝突を別名で避けた取込は Warning で残す。同日・別時刻の正当なケースでも起きるので
            // トーストにはせず、near-dup を疑うときに gui.log から辿れるようにする(ADR-0015)。
            foreach (var item in report.Items.Where(i => i.Outcome == IngestOutcome.Imported && i.Message is not null))
                _log.LogWarning("{Message}: {Output}", item.Message, item.OutputPath);

            // 走査ごとに status スナップショットを更新(in-process 制御)。0 件走査でも
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

    /// <summary>再適用の実行 1 つ。<see cref="Plan"/> が null なら確認(dry-run)、あれば実行。</summary>
    // Plan があれば実行、無ければ確認。確認で Draft があれば、番組の設定はディスクではなくそれを使う(#154)。
    // ReloadFirst は、実行の前に同じジョブの中で設定リロードする(管理画面の「保存して適用」。#154)。
    private sealed record ReapplyJob(ReapplyReport? Plan, ProgramMasterConfig? Draft, bool ReloadFirst = false)
    {
        // 待っているのは UI スレッド。ループのスレッドで続きを走らせない。
        public TaskCompletionSource<ReapplyResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
