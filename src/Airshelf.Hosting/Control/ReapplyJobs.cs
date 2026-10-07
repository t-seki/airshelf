using System.Diagnostics.CodeAnalysis;
using Airshelf.Core.Configuration;
using Airshelf.Core.Reapply;

namespace Airshelf.Hosting.Control;

/// <summary>再適用のジョブの種類(<see cref="ReapplyJob"/>)。</summary>
public enum ReapplyJobKind
{
    /// <summary>保存済みの設定で確かめる(トレイ・管理画面。設定リロードの反映の後に dry-run。#126)。</summary>
    Plan,

    /// <summary>保存前の番組の設定(下書き)で確かめる(管理画面の番組ページ。#154)。</summary>
    PlanDraft,

    /// <summary>直前の確認を実行する(#126)。</summary>
    Apply,

    /// <summary>設定リロード → 直前の確認を実行する(管理画面の「保存して適用」。#154)。</summary>
    ReloadAndApply,

    /// <summary>CLI の確認(#181)。設定リロードはせず、ディスクの番組・放送局の設定で dry-run し、予定 ID を付けて返す。</summary>
    PlanForCli,

    /// <summary>CLI の実行(#181)。設定リロード → dry-run し直し → 予定 ID が一致したときだけ実行する。</summary>
    ApplyPlanId,
}

/// <summary>
/// 再適用のジョブ 1 つ。直列ループが <see cref="ReapplyJobs.RunAsync"/> で処理し、<see cref="Completion"/> を必ず結果で完了させる。
/// </summary>
/// <param name="Plan"><see cref="ReapplyJobKind.Apply"/> / <see cref="ReapplyJobKind.ReloadAndApply"/> で実行する確認。</param>
/// <param name="Draft"><see cref="ReapplyJobKind.PlanDraft"/> の番組の設定。</param>
/// <param name="PlanId"><see cref="ReapplyJobKind.ApplyPlanId"/> の予定 ID(<see cref="ReapplyPlanId.IsWellFormed"/> な値)。</param>
public sealed record ReapplyJob(
    ReapplyJobKind Kind, ReapplyReport? Plan = null, ProgramMasterConfig? Draft = null, string? PlanId = null)
{
    // 待っているのは UI スレッドか制御チャネル。ループのスレッドで続きを走らせない。
    public TaskCompletionSource<ReapplyResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// <see cref="ReapplyJobs"/> が常駐(<c>RadioMonitorWorker</c>)に頼むこと。設定の差し替え・再適用の組み立て・ログは常駐が持つ。
/// どれも直列ループの上から呼ばれる。
/// </summary>
public interface IReapplyJobHost
{
    /// <summary>今の設定(設定リロードで差し替わる)。</summary>
    AirshelfConfig Config { get; }

    /// <summary>直近の設定リロードの反映に失敗した理由(成功なら null)。</summary>
    string? LastReloadError { get; }

    /// <summary>
    /// 直近の設定リロードの反映の失敗が、台帳のある場所に届かなかったためか(<see cref="LedgerUnreachableException"/>。#228)。
    /// 成功・ほかの理由の失敗(programs.json が壊れている等)なら false。
    /// </summary>
    bool LastReloadUnreachable { get; }

    /// <summary>
    /// 設定ファイル(radirec.json)を読んで検証する(反映はしない)。<paramref name="error"/> は前置き
    /// (「設定の読み込みに失敗しました: 」など)を含んだ、そのまま出せる理由。
    /// </summary>
    bool TryLoadConfig(out AirshelfConfig? config, out string? error);

    /// <summary>
    /// 読んだ設定を反映する(取り込みのグラフを組み直す)。組み直せなければ旧構成のまま、<see cref="LastReloadError"/> に理由を残す。
    /// あわせて <see cref="LastReloadUnreachable"/> も更新する(台帳を開けなかったときだけ true。成功なら false)。
    /// </summary>
    void ApplyReload(AirshelfConfig config);

    /// <summary>ディスクの番組・放送局の設定を読む(<c>programs.json</c> / <c>stations.json</c>)。読めなければ例外。</summary>
    ReapplySettings LoadSettings(AirshelfConfig config);

    /// <summary>ディスクの放送局の設定だけを読む(番組の設定は下書きを使うとき)。読めなければ例外。</summary>
    StationMasterConfig LoadStations(AirshelfConfig config);

    /// <summary>
    /// 読み込み済みの設定で再適用を組み立てて走らせる。組み立て(台帳オープン等)の失敗は <c>Failure</c> で返す
    /// (台帳のある場所に届かなければ <see cref="ReapplyResult.Unreachable"/>、ほかは <see cref="ReapplyResult.Failed"/>。#223)。
    /// </summary>
    Task<(ReapplyReport? Report, ReapplyResult? Failure)> RunServiceAsync(
        AirshelfConfig config, ReapplySettings settings, bool dryRun, CancellationToken cancellationToken);

    /// <summary>再適用の結果の明細を gui.log に書く。</summary>
    void LogReport(ReapplyReport report);

    void LogWarning(string message);

    void LogError(Exception exception, string message);
}

/// <summary>
/// 再適用の確認・実行のジョブの中身(ADR-0016 / #126 / #154 / #181)。常駐の直列ループの上で 1 つずつ処理する。
/// 管理画面・トレイの「直前の確認」(<see cref="_lastPlan"/>)を持ち、実行はその確認に対してだけ行う。
/// CLI の確認と実行(<see cref="ReapplyJobKind.PlanForCli"/> / <see cref="ReapplyJobKind.ApplyPlanId"/>)は、直前の確認に触れない
/// (ADR-0020。CLI の確認が、管理画面で開いている確認を古くしない)。
/// </summary>
public sealed class ReapplyJobs(IReapplyJobHost host)
{
    private const string RetryReapply = "もう一度確認からやり直してください";
    private const string KeepsRunning = "取り込みは前の設定のまま動いています";

    // 直近の再適用の確認(dry-run)と、そのときの config・番組/放送局の設定。実行はこの確認に対してだけ行い、
    // 番組/放送局の設定も読み直さずにこれを使う(直列ループのみが読み書きする)。
    private (ReapplyReport Plan, AirshelfConfig Config, ReapplySettings Settings)? _lastPlan;

    /// <summary>管理画面・トレイの直前の確認があるか(テスト用)。</summary>
    public bool HasLastPlan => _lastPlan is not null;

    /// <summary>ジョブを 1 つ処理する。例外は呼び出し側(<see cref="SerialLoop.RunStepAsync"/>)が失敗の結果にする。</summary>
    public Task<ReapplyResult> RunAsync(ReapplyJob job, CancellationToken cancellationToken) => job.Kind switch
    {
        ReapplyJobKind.Plan => PlanAsync(draft: null, cancellationToken),
        ReapplyJobKind.PlanDraft => PlanAsync(job.Draft, cancellationToken),
        ReapplyJobKind.Apply => ApplyAsync(job.Plan!, cancellationToken),
        ReapplyJobKind.ReloadAndApply => ReloadAndApplyAsync(job.Plan!, cancellationToken),
        ReapplyJobKind.PlanForCli => PlanForCliAsync(cancellationToken),
        ReapplyJobKind.ApplyPlanId => ApplyPlanIdAsync(job.PlanId!, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(job), job.Kind, "知らない再適用のジョブ"),
    };

    // 確認:今の config(reload 反映後)で番組/放送局の設定を読み、dry-run する。読んだ設定は実行に持ち越す。
    // draft があれば(管理画面の番組ページ。#154)、番組の設定はディスクの programs.json ではなくそれを使う。
    private async Task<ReapplyResult> PlanAsync(ProgramMasterConfig? draft, CancellationToken cancellationToken)
    {
        _lastPlan = null;

        // 設定リロードが反映できなかった(programs.json の JSON が壊れている等)。取り込みは旧構成のまま
        // 稼働しているので、再適用だけを新しい設定で走らせることはせず中止する(ADR-0016)。
        // 下書きで確かめる(#154)ときは止めない。実行の段(ReloadAndApplyAsync)で必ずリロードを試し直し、失敗なら実行しない。
        if (ReapplyPlanGate.BlockingReloadError(hasDraft: draft is not null, host.LastReloadError) is { } reloadError)
        {
            host.LogWarning($"統合設定の再適用: 設定の反映に失敗したため中止しました: {reloadError}");
            return ReapplyResult.Failed($"設定の読み込みに失敗しました: {reloadError}");
        }

        var config = host.Config;
        ReapplySettings settings;
        try
        {
            settings = draft is null
                ? host.LoadSettings(config)
                : new ReapplySettings(host.LoadStations(config), draft);
        }
        catch (Exception ex)
        {
            host.LogError(ex, "統合設定の再適用: 番組・放送局の設定の読み込みに失敗しました");
            return ReapplyResult.Failed($"設定の読み込みに失敗しました: {ex.Message}");
        }

        var (plan, failure) = await host.RunServiceAsync(config, settings, dryRun: true, cancellationToken);
        if (plan is null)
            return failure!;

        host.LogReport(plan);
        _lastPlan = (plan, config, settings);
        return ReapplyResult.Ok(plan);
    }

    // 実行:確認のときの config・番組/放送局の設定で、まず dry-run を計算し直し、確認した予定と同じときだけ実行する。
    // 確認ダイアログを開いている間に programs.json が編集された・取り込みでファイルや台帳が変わった等で予定が
    // ずれていれば、見せていない変更をしないよう中止する。
    private async Task<ReapplyResult> ApplyAsync(ReapplyReport plan, CancellationToken cancellationToken)
    {
        if (_lastPlan is not { } last || !ReferenceEquals(last.Plan, plan))
            return ReapplyResult.Stale($"確認した内容が古くなったため中止しました。{RetryReapply}");

        // 実行に使えるのは直前の確認だけ。ここで忘れる(同じ確認で 2 回実行しない・中止した確認を再利用しない)。
        _lastPlan = null;

        if (!ReferenceEquals(last.Config, host.Config))
            return ReapplyResult.Stale($"確認のあとに設定が変わったため中止しました。{RetryReapply}");

        return await ReplanAndRunAsync(plan, last.Config, last.Settings, cancellationToken);
    }

    // 保存した設定での実行(管理画面の「保存して適用」。#154・ADR-0016):設定リロード → 読み直した設定が確認のときと
    // 値で同じかを確かめる → dry-run し直して予定が同じなら実行。1 つのジョブの中で続けて行うので、リロードと再適用の間に
    // 取り込みの走査は挟まらない(再適用は新しい設定・取り込みは古い設定、という食い違いを作らない)。
    // リロードに失敗したら再適用はしない(取り込みは前の設定のまま)。どこで止まっても、もう一度確かめてから呼び直せる。
    private async Task<ReapplyResult> ReloadAndApplyAsync(ReapplyReport plan, CancellationToken cancellationToken)
    {
        if (_lastPlan is not { } last || !ReferenceEquals(last.Plan, plan))
            return ReapplyResult.Stale($"確認した内容が古くなったため中止しました。{RetryReapply}");
        _lastPlan = null;

        // 1. 設定リロード(RequestReload と同じ検証 → ApplyReload と同じ組み直し。ここは直列ループの上なので直接反映する)。
        // 2. 読み直した番組・放送局の設定を読む。
        if (!TryReloadThenLoadSettings(out var config, out var settings, out var failure))
            return failure;

        // 読み直した番組・放送局の設定が、確かめたとき(下書き)と値で同じか。
        if (!settings.HasSameValuesAs(last.Settings))
        {
            host.LogWarning("統合設定の再適用: 読み直した番組・放送局の設定が確認のときと違うため中止しました");
            return ReapplyResult.Stale($"確認のあとに設定が変わったため中止しました。{RetryReapply}");
        }

        // 3. 読み直した設定で dry-run し直し、予定が同じなら実行する。
        return await ReplanAndRunAsync(plan, config, settings, cancellationToken);
    }

    // 実行の直前に dry-run を計算し直し、確認した予定と同じ(値で)ときだけ実行する。
    private async Task<ReapplyResult> ReplanAndRunAsync(
        ReapplyReport plan, AirshelfConfig config, ReapplySettings settings, CancellationToken cancellationToken)
    {
        var (replanned, replanFailure) = await host.RunServiceAsync(config, settings, dryRun: true, cancellationToken);
        if (replanned is null)
            return replanFailure!;
        if (!replanned.HasSamePlanAs(plan))
        {
            host.LogWarning("統合設定の再適用: 確認のあとに状態が変わったため中止しました(実行直前の予定が確認と違います)");
            host.LogReport(replanned);
            return ReapplyResult.Stale($"確認のあとに状態が変わったため中止しました(取り込みや設定の変更で予定が変わりました)。{RetryReapply}");
        }

        return await RunAsync(config, settings, cancellationToken);
    }

    // CLI の確認(#181・ADR-0020):設定リロードはせず、今の config とディスクの番組・放送局の設定で dry-run し、予定 ID を付けて返す。
    // 常駐の状態(config・直前の確認)は変えない。管理画面で開いている確認を古くしない(リロードすると config が差し替わり、
    // ApplyAsync の ReferenceEquals(last.Config, host.Config) で IsStale になる)。
    // 直近の設定リロードの反映の失敗では止めない(下書きで確かめるときと同じ)。ディスクの設定が壊れていれば、読み込みで失敗する。
    // 実行(ApplyPlanIdAsync)は必ずリロードを試し直し、失敗なら実行しない。
    private async Task<ReapplyResult> PlanForCliAsync(CancellationToken cancellationToken)
    {
        var config = host.Config;
        ReapplySettings settings;
        try
        {
            settings = host.LoadSettings(config);
        }
        catch (Exception ex)
        {
            host.LogError(ex, "統合設定の再適用(CLI の確認): 番組・放送局の設定の読み込みに失敗しました");
            return ReapplyResult.Failed($"設定の読み込みに失敗しました: {ex.Message}");
        }

        var (plan, failure) = await host.RunServiceAsync(config, settings, dryRun: true, cancellationToken);
        if (plan is null)
            return failure!;

        host.LogReport(plan); // 明細はトレイの確認と同じく gui.log にも書く
        return ReapplyResult.Planned(plan);
    }

    // CLI の実行(#181・ADR-0020):設定リロード → 読み直した設定で dry-run → 予定 ID が一致したときだけ実行する。1 つのジョブの中で
    // 続けて行うので、間に取り込みの走査は挟まらない。直前の確認(_lastPlan)には触れない。
    // 一致しなければ実行せず、dry-run し直した新しい予定と予定 ID を返す(エージェントが違いを見て、新しい ID で頼み直せる)。
    private async Task<ReapplyResult> ApplyPlanIdAsync(string planId, CancellationToken cancellationToken)
    {
        if (!TryReloadThenLoadSettings(out var config, out var settings, out var failure))
            return failure;

        var (replanned, replanFailure) = await host.RunServiceAsync(config, settings, dryRun: true, cancellationToken);
        if (replanned is null)
            return replanFailure!;
        if (ReapplyPlanId.Of(replanned) != ReapplyPlanId.Normalize(planId))
        {
            host.LogWarning("統合設定の再適用(CLI): 予定 ID が今の予定と違うため実行しませんでした");
            host.LogReport(replanned);
            return ReapplyResult.PlanChanged(
                "確かめた予定から変わったため実行しませんでした(設定・取り込みの変更で予定が変わりました)。" +
                "新しい予定を確かめて、その予定 ID で頼み直してください", replanned);
        }

        return await RunAsync(config, settings, cancellationToken);
    }

    // 設定リロード(ファイルから読み直して反映)→ 番組・放送局の設定を読む。どこかで失敗したら、実行しない理由を failure に返す
    // (取り込みは前の設定のまま)。
    private bool TryReloadThenLoadSettings(
        [NotNullWhen(true)] out AirshelfConfig? config,
        [NotNullWhen(true)] out ReapplySettings? settings,
        [NotNullWhen(false)] out ReapplyResult? failure)
    {
        config = null;
        settings = null;
        failure = null;
        if (!host.TryLoadConfig(out var newConfig, out var loadError))
        {
            // loadError は読み込みの戻り値(「設定の読み込みに失敗しました: …」「設定ファイルが見つかりません: …」)で、
            // 前置きを含む。gui.log の行も結果も、reload の拒否と同じくそのまま出す(もう一度前置きを付けると 2 回続く。#218)。
            host.LogWarning($"統合設定の再適用を中止しました: {loadError}");
            failure = ReapplyResult.Failed($"{loadError}({KeepsRunning})");
            return false;
        }
        host.ApplyReload(newConfig!);
        if (host.LastReloadError is { } reloadError || !ReferenceEquals(host.Config, newConfig))
        {
            var reason = host.LastReloadError ?? "設定を差し替えられませんでした";
            host.LogWarning($"統合設定の再適用: 設定の反映に失敗したため中止しました: {reason}");
            var error = $"設定の反映に失敗しました: {reason}({KeepsRunning})";
            // 反映(取り込みのグラフの組み直し)は書き込み用に台帳を開き、フォルダが無ければ作る(初めて使うとき)。開けなかったときだけ
            // 常駐が「届かない」と記録する(LedgerFile.OpenWritable の LedgerUnreachableException)ので、ほかのコマンドと同じく
            // 「届かない」にする(#223)。失敗の後に台帳の場所を確かめる作りだと、programs.json が壊れていて台帳のフォルダがまだ
            // 作られていないときにも「届かない」と取り違えた(#228)。
            failure = host.LastReloadUnreachable ? ReapplyResult.Unreachable(error) : ReapplyResult.Failed(error);
            return false;
        }
        try
        {
            settings = host.LoadSettings(host.Config);
        }
        catch (Exception ex)
        {
            host.LogError(ex, "統合設定の再適用: 番組・放送局の設定の読み込みに失敗しました");
            failure = ReapplyResult.Failed($"設定の読み込みに失敗しました: {ex.Message}");
            return false;
        }
        config = host.Config;
        return true;
    }

    // 予定を確かめ終えた後の実行。
    private async Task<ReapplyResult> RunAsync(AirshelfConfig config, ReapplySettings settings, CancellationToken cancellationToken)
    {
        var (result, failure) = await host.RunServiceAsync(config, settings, dryRun: false, cancellationToken);
        if (result is null)
            return failure!;

        host.LogReport(result);
        return ReapplyResult.Ok(result);
    }
}
