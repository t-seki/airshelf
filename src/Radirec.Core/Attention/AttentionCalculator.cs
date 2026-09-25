using Radirec.Core.Configuration;
using Radirec.Core.Ledger;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;

namespace Radirec.Core.Attention;

/// <summary>
/// 要対応(CONTEXT.md)の計算に使う番組の設定。
/// </summary>
/// <param name="Programs">
/// <c>programs.json</c> の中身。<c>radirec.json</c> で programs.json を使わない設定(<c>ProgramsFile</c> が空)なら null。
/// </param>
public sealed record AttentionPrograms(ProgramMasterConfig? Programs)
{
    /// <summary>programs.json を使う設定か。使わない設定では、統合も「分けておく」も記録できない。</summary>
    public bool UsesProgramsFile => Programs is not null;
}

/// <summary>いま判断を待っている事柄の一覧(ADR-0018)。表示するたびに計算し直す。</summary>
public sealed record AttentionItems
{
    /// <summary>
    /// 統合設定の無い兄弟フォルダの組。programs.json を使わない設定では空(出しても解消できないため。#155 の決定 1)。
    /// </summary>
    public IReadOnlyList<UnintegratedSiblingGroup> UnintegratedSiblings { get; init; } = [];
}

/// <summary>
/// 要対応を、台帳と番組・放送局の設定だけから計算する(ADR-0018。ファイルには触れない)。台帳の回を今の設定で
/// 解決し直すのは再適用と同じ <see cref="LedgerEpisodeResolver"/> で行う。
/// </summary>
public static class AttentionCalculator
{
    /// <param name="ledger">台帳の全記録。</param>
    /// <param name="resolver">今の放送局・番組の設定で作った解決器(programs.json を使わない設定なら空の番組の設定で作る)。</param>
    /// <param name="programs">番組の設定(分けておく組を読む)。</param>
    public static AttentionItems Compute(
        IEnumerable<LedgerEntry> ledger, LedgerEpisodeResolver resolver, AttentionPrograms programs)
    {
        var resolved = resolver.ResolveAll(ledger).ToList();

        var siblings = programs.Programs is { } config
            ? UnintegratedSiblings.Find(
                resolved.Select(r => r.Folder).OfType<ProgramFolder>(), resolver.ProgramResolver, config.KeepSeparate)
            : [];

        return new AttentionItems { UnintegratedSiblings = siblings };
    }
}
