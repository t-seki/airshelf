using Radirec.Core.Reapply;

namespace Radirec.Cli.Core.Output;

/// <summary><c>reapply --json</c> の <c>result</c>。</summary>
/// <param name="Aligned">取り込み済みの回が今の設定と揃っているか(変える回も失敗も無い。対象外だけなら true)。</param>
/// <param name="DuplicatesRewritingTags">重複の回のうち、移動先のファイルのタグも書き換える回の数。</param>
/// <param name="UnintegratedSiblings">統合設定の無い兄弟フォルダの組(programs.json で統合するか確かめる)。</param>
/// <param name="PlanId">
/// 予定 ID(#181)。dry-run では <c>reapply --apply &lt;予定 ID&gt;</c> に渡す値。予定が変わった(終了コード 3)ときは新しい予定の予定 ID、
/// 実行したときは実行した予定の予定 ID。
/// </param>
public sealed record ReapplyJson(
    bool DryRun,
    bool Aligned,
    ReapplyCountsJson Counts,
    int DuplicatesRewritingTags,
    IReadOnlyList<ReapplyAlbumSummary> Albums,
    IReadOnlyList<ReapplyItemJson> Items,
    IReadOnlyList<UnintegratedSiblingJson> UnintegratedSiblings,
    LibraryRefreshJson LibraryRefresh,
    string? PlanId)
{
    public static ReapplyJson From(ReapplyReport report, string? planId) => new(
        report.DryRun,
        report.IsAligned,
        new ReapplyCountsJson(
            report.Count(ReapplyAction.Move),
            report.Count(ReapplyAction.LedgerAndTags),
            report.Count(ReapplyAction.LedgerOnly),
            report.Count(ReapplyAction.DuplicateLeft),
            report.Count(ReapplyAction.Excluded),
            report.Count(ReapplyAction.Failed)),
        report.DuplicatesRewritingTags,
        report.Albums,
        report.Items.Select(i => new ReapplyItemJson(
            i.Action, i.Station, i.FromAlbum, i.ToAlbum, i.BroadcastDate, i.BroadcastTime,
            i.FromPath, i.ToPath, i.SourcePath, i.Reason, i.RewritesTags)).ToList(),
        report.UnintegratedSiblings.Select(UnintegratedSiblingJson.From).ToList(),
        new LibraryRefreshJson(report.LibraryRefresh, report.RefreshError),
        planId);
}

/// <summary>扱いごとの件数(表の下の 1 行と同じ)。</summary>
public sealed record ReapplyCountsJson(int Move, int LedgerAndTags, int LedgerOnly, int DuplicateLeft, int Excluded, int Failed);

/// <param name="Action">扱い(<c>move</c>・<c>ledgerAndTags</c>・<c>ledgerOnly</c>・<c>duplicateLeft</c>・<c>excluded</c>・<c>failed</c>)。</param>
/// <param name="Reason">対象外・失敗の理由、重複の注記。無ければ null。</param>
/// <param name="RewritesTags">置き場所を変えずにタグ(album / title / track)を書き換えるか。</param>
public sealed record ReapplyItemJson(
    ReapplyAction Action, string Station, string FromAlbum, string ToAlbum,
    DateOnly BroadcastDate, TimeOnly? BroadcastTime,
    string FromPath, string ToPath, string SourcePath, string? Reason, bool RewritesTags);

/// <param name="Folders">組のフォルダ(パートの順)。</param>
/// <param name="FoldersWithoutSetting">組のうち、programs.json に統合の設定が無いフォルダ。</param>
/// <param name="MergedAlbum">組の一部がすでに入っている統合番組のアルバム名。無ければ null。</param>
/// <param name="FoldersNewToKeptApart">
/// 分けておく組(<c>KeepSeparate</c>)に記録した組に、あとから加わったフォルダ(組が変わったので判断し直す)。記録していなければ空。
/// </param>
public sealed record UnintegratedSiblingJson(
    string Station, IReadOnlyList<string> Folders, IReadOnlyList<string> FoldersWithoutSetting, string? MergedAlbum,
    IReadOnlyList<string> FoldersNewToKeptApart)
{
    public static UnintegratedSiblingJson From(UnintegratedSiblingGroup sibling) => new(
        sibling.Group.Station, sibling.Group.Folders, sibling.FoldersWithoutSetting, sibling.MergedAlbum,
        sibling.FoldersNewToKeptApart);
}
