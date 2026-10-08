using Airshelf.Core.Configuration;
using Airshelf.Core.Normalization;

namespace Airshelf.Core.Reapply;

/// <summary>
/// 統合設定の無い兄弟フォルダの組を探す(再適用の確認と、管理画面の要対応で共有する。ADR-0016 / ADR-0018。純粋)。
/// </summary>
/// <remarks>
/// 「統合設定が無い」はフォルダ単位(<c>programs.json</c> の <c>Match.Folders</c> に無い)で判定する(#127 と同じ基準)。
/// 分けておく組(<c>KeepSeparate</c>)に記録した組は出さない。ただし記録した後に組へ新しいフォルダが加わったら、
/// 組が変わったのでもう一度出す(<see cref="UnintegratedSiblingGroup.FoldersNewToKeptApart"/>)。
/// </remarks>
public static class UnintegratedSiblings
{
    /// <param name="knownFolders">知っているフォルダ(台帳にあるらじれこの回の番組フォルダ)。</param>
    /// <param name="programResolver">今の番組の設定の解決器。</param>
    /// <param name="keepSeparate">分けておく組(<c>programs.json</c> の <c>KeepSeparate</c>)。</param>
    public static IReadOnlyList<UnintegratedSiblingGroup> Find(
        IEnumerable<ProgramFolder> knownFolders, IProgramResolver programResolver, IEnumerable<KeptApartSiblings> keepSeparate)
    {
        var kept = keepSeparate.ToList();
        var groups = new List<UnintegratedSiblingGroup>();
        foreach (var group in SiblingFolders.Find(knownFolders))
        {
            var withoutSetting = group.Folders.Where(f => programResolver.Resolve(group.Station, f).UsedFallback).ToList();
            if (withoutSetting.Count == 0)
                continue;

            var keptKeys = KeptFolderKeys(group, kept);
            var newToKept = keptKeys is null
                ? []
                : group.Folders.Where(f => !keptKeys.Contains(TextNormalizer.ToMatchKey(f))).ToList();
            // 分けておく組に記録したとおりなら出さない。
            if (keptKeys is not null && newToKept.Count == 0)
                continue;

            groups.Add(new UnintegratedSiblingGroup(group, withoutSetting)
            {
                // 統合番組が 2 つ以上に分かれていたら、パートの順で最初のフォルダが入っているほう(#144 の提案と同じ)。
                MergedAlbum = SiblingFolders.OrderAsParts(group.Folders)
                    .Select(f => programResolver.Resolve(group.Station, f))
                    .FirstOrDefault(r => !r.UsedFallback)?.Canonical,
                FoldersNewToKeptApart = newToKept,
            });
        }
        return groups;
    }

    // この組と重なる分けておく組のフォルダ(照合キー)。重なる組が無ければ null(分けておく組に記録していない)。
    private static HashSet<string>? KeptFolderKeys(SiblingFolderGroup group, IReadOnlyList<KeptApartSiblings> kept)
    {
        var stationKey = TextNormalizer.ToMatchKey(group.Station);
        var groupKeys = group.Folders.Select(TextNormalizer.ToMatchKey).ToHashSet();
        HashSet<string>? keys = null;
        foreach (var entry in kept.Where(k => TextNormalizer.ToMatchKey(k.Match.Station) == stationKey))
        {
            var entryKeys = entry.Match.Folders.Select(TextNormalizer.ToMatchKey).ToList();
            if (!entryKeys.Any(groupKeys.Contains))
                continue;
            keys ??= [];
            keys.UnionWith(entryKeys);
        }
        return keys;
    }
}
