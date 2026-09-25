using Radirec.Core.Reapply;

namespace Radirec.Hosting.Control;

/// <summary>
/// 統合設定の再適用(ADR-0016 / #126)の結果。dry-run(確認)と実行のどちらにも使う。
/// 受理されなかった(取り込み停止中・設定の読み込み失敗・再適用の組み立て失敗等)ときは
/// <see cref="Report"/> が null で <see cref="Error"/> に理由が入る。
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

    public static ReapplyResult Ok(ReapplyReport report) => new() { Report = report };
    public static ReapplyResult Failed(string error) => new() { Error = error };
    public static ReapplyResult Stale(string error) => new() { Error = error, IsStale = true };
}
