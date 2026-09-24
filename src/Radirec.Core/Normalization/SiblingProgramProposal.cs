namespace Radirec.Core.Normalization;

/// <summary>
/// 兄弟フォルダの 1 組から作る、統合設定(<c>programs.json</c> の 1 エントリ)の提案(#144)。
/// </summary>
/// <param name="Station">放送局の正規名(<c>Match.Station</c>)。</param>
/// <param name="Album">
/// 新しいエントリなら <c>Canonical.Album</c>(末尾の印を除いた名前)。兄弟が既に統合番組に入っていれば、その統合番組のアルバム名。
/// </param>
/// <param name="Folders">
/// 新しいエントリなら組の全フォルダをパートの順に(<c>Match.Folders</c>)。既存の統合番組へ足すなら、統合設定の無いフォルダだけ。
/// </param>
/// <param name="JoinsExisting">
/// 兄弟が既に統合番組に入っているか。このときは既存のエントリに触れず、<see cref="Folders"/> を
/// その統合番組の <c>Folders</c> の末尾に足すよう案内するだけにする。
/// </param>
public sealed record SiblingProgramProposal(string Station, string Album, IReadOnlyList<string> Folders, bool JoinsExisting);

/// <summary>
/// 統合設定の無い兄弟フォルダの組から、統合設定の提案を組み立てる(#144。純粋)。印の扱いと並び順は
/// <see cref="SiblingFolders"/> の規則を使う。
/// </summary>
public static class SiblingProgramProposals
{
    /// <param name="groups">兄弟フォルダの組(再適用の dry-run が見つけた組)。</param>
    /// <param name="resolver">
    /// 追記する先の <c>programs.json</c> の**今の中身**から作った解決器。「統合設定が無い」はこれで決め直す
    /// (dry-run の後に利用者が programs.json を直していることがあるため)。統合設定の無いフォルダが残らない組は提案しない。
    /// </param>
    public static IReadOnlyList<SiblingProgramProposal> Build(IEnumerable<SiblingFolderGroup> groups, IProgramResolver resolver)
    {
        var proposals = new List<SiblingProgramProposal>();
        foreach (var group in groups)
        {
            var ordered = SiblingFolders.OrderAsParts(group.Folders);
            var resolutions = ordered.Select(f => (Folder: f, Resolution: resolver.Resolve(group.Station, f))).ToList();

            var withoutSetting = resolutions.Where(r => r.Resolution.UsedFallback).Select(r => r.Folder).ToList();
            if (withoutSetting.Count == 0)
                continue;

            // 兄弟が統合番組に入っている:既存のエントリには触れず、足すフォルダだけを示す。
            // 統合番組が 2 つ以上に分かれていたら、パートの順で最初のフォルダが入っているほうへ案内する。
            if (resolutions.FirstOrDefault(r => !r.Resolution.UsedFallback) is { Folder: not null } merged)
            {
                proposals.Add(new SiblingProgramProposal(group.Station, merged.Resolution.Canonical, withoutSetting, JoinsExisting: true));
                continue;
            }

            proposals.Add(new SiblingProgramProposal(
                group.Station, SiblingFolders.OriginalBaseName(ordered[0]), ordered, JoinsExisting: false));
        }
        return proposals;
    }
}
