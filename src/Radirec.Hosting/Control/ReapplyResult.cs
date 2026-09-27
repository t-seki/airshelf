using Radirec.Core.Reapply;

namespace Radirec.Hosting.Control;

/// <summary>
/// 統合設定の再適用(ADR-0016 / #126)の結果。dry-run(確認)と実行のどちらにも使う。
/// 受理されなかった(取り込み停止中・設定の読み込み失敗・再適用の組み立て失敗等)ときは
/// <see cref="Report"/> が null で <see cref="Error"/> に理由が入る。例外は、CLI の実行で予定 ID が合わなかったとき
/// (<see cref="PlanChanged"/>):実行はせず、<see cref="Report"/> に dry-run し直した新しい予定が入る。
/// </summary>
public sealed record ReapplyResult
{
    /// <summary>再適用の結果。失敗・中止時は null。</summary>
    public ReapplyReport? Report { get; init; }

    /// <summary>中止した理由(利用者に見せる文言)。成功時は null。</summary>
    public string? Error { get; init; }

    /// <summary>
    /// 確認した後に設定・状態が変わったため、実行しなかった(確認の予定と、実行しようとした予定が違う)。
    /// 利用者には「もう一度確認する」ことを求める(管理画面の W-278)。
    /// </summary>
    public bool IsStale { get; init; }

    /// <summary>
    /// 取り込みが止まっている・止まったので、受理しなかった(または途中で中止した)。CLI はこれを終了コード 2 にする(ADR-0020)。
    /// </summary>
    public bool IsStopped { get; init; }

    /// <summary>
    /// 再適用の準備で、外(台帳のある NAS など)に届かなかった(<see cref="LedgerUnreachableException"/>)。CLI はこれを終了コード 5 に
    /// する(ADR-0020。ほかのコマンドの「NAS に届かない → 5」と同じ。#223)。古いトレイの応答には無く、false として読まれる(1 のまま)。
    /// </summary>
    public bool IsUnreachable { get; init; }

    /// <summary>
    /// <see cref="Report"/> の予定 ID(<see cref="ReapplyPlanId"/>)。CLI の <c>reapply</c>(#181)の確認と、予定 ID が合わなかった
    /// ときの新しい予定(<see cref="IsStale"/> で <see cref="Report"/> あり)にだけ入る。管理画面の確認・実行では null。
    /// </summary>
    public string? PlanId { get; init; }

    public static ReapplyResult Ok(ReapplyReport report) => new() { Report = report };
    public static ReapplyResult Failed(string error) => new() { Error = error };
    public static ReapplyResult Stale(string error) => new() { Error = error, IsStale = true };
    public static ReapplyResult Stopped(string error) => new() { Error = error, IsStopped = true };
    public static ReapplyResult Unreachable(string error) => new() { Error = error, IsUnreachable = true };

    /// <summary>CLI の確認の結果(予定と、その予定 ID)。</summary>
    public static ReapplyResult Planned(ReapplyReport plan) => new() { Report = plan, PlanId = ReapplyPlanId.Of(plan) };

    /// <summary>
    /// CLI の実行で、予定 ID が合わなかった結果。実行はしていない。<paramref name="replanned"/> は実行の直前に dry-run し直した予定で、
    /// エージェントがもう一度 dry-run しなくても違いを見て、新しい予定 ID で頼み直せるように返す。
    /// </summary>
    public static ReapplyResult PlanChanged(string error, ReapplyReport replanned) =>
        new() { Error = error, IsStale = true, Report = replanned, PlanId = ReapplyPlanId.Of(replanned) };
}
