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

    /// <summary>
    /// 解決していない取込失敗(失敗のストアにある失敗)。記録した時刻の新しい順で、時刻を持たない(#172 の移行前の)
    /// 失敗は後ろに並べる。同じファイルの取り込みに成功するか、取込元から消えれば、ストアから消えてここにも出ない。
    /// </summary>
    public IReadOnlyList<IngestFailureRecord> UnresolvedFailures { get; init; } = [];

    /// <summary>
    /// 番組の設定と食い違ったままの取り込み済みの回を、局・今のアルバム → 設定のアルバムごとにまとめたもの(W-134 / W-135)。
    /// 再適用すれば消える(再適用しても衝突などで揃わない回は、実際に揃っていないので出し続ける)。
    /// </summary>
    public IReadOnlyList<SettingsDriftGroup> SettingsDrift { get; init; } = [];
}

/// <summary>
/// 番組の設定と食い違ったままの回の 1 まとまり(W-135 の 1 行)。
/// </summary>
/// <param name="Station">台帳に記録した放送局(今の置き場所の局)。</param>
/// <param name="FromAlbum">台帳に記録したアルバム名(今のアルバム)。</param>
/// <param name="ToStation">今の設定で解決した放送局(ふつうは <paramref name="Station"/> と同じ。stations.json で局の正規名を変えたときに違う)。</param>
/// <param name="ToAlbum">今の設定で解決したアルバム名(設定のアルバム)。パートだけが違う回は <paramref name="FromAlbum"/> と同じ。</param>
/// <param name="Count">この組の回数。</param>
public sealed record SettingsDriftGroup(string Station, string FromAlbum, string ToStation, string ToAlbum, int Count);

/// <summary>
/// 要対応を、台帳・失敗のストアと番組・放送局の設定だけから計算する(ADR-0018。ファイルには触れない)。台帳の回を今の設定で
/// 解決し直すのは再適用と同じ <see cref="LedgerEpisodeResolver"/> で行い、「記録どおりか」の判定
/// (<see cref="ResolvedLedgerEntry.IsAsRecorded"/>)も再適用と共有する(再適用の dry-run は出力先の <c>File.Exists</c> を
/// 呼ぶので NAS に触れる。表示のたびに走らせるには重いため、判定だけを使う)。
/// </summary>
public static class AttentionCalculator
{
    /// <param name="ledger">台帳の全記録。</param>
    /// <param name="resolver">今の放送局・番組の設定で作った解決器(programs.json を使わない設定なら空の番組の設定で作る)。</param>
    /// <param name="programs">番組の設定(分けておく組を読む)。</param>
    /// <param name="failures">失敗のストアの全件。省略すると空。</param>
    public static AttentionItems Compute(
        IEnumerable<LedgerEntry> ledger, LedgerEpisodeResolver resolver, AttentionPrograms programs,
        IEnumerable<IngestFailureRecord>? failures = null)
    {
        var resolved = resolver.ResolveAll(ledger).ToList();

        var siblings = programs.Programs is { } config
            ? UnintegratedSiblings.Find(
                resolved.Select(r => r.Folder).OfType<ProgramFolder>(), resolver.ProgramResolver, config.KeepSeparate)
            : [];

        return new AttentionItems
        {
            UnintegratedSiblings = siblings,
            UnresolvedFailures = (failures ?? [])
                .OrderByDescending(f => f.FailedAtUtc.HasValue)
                .ThenByDescending(f => f.FailedAtUtc)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ToList(),
            SettingsDrift = Drift(resolved),
        };
    }

    // 記録どおりでない回(再適用が揃える回)を、局・今のアルバム → 設定のアルバムごとに数える。名前を解析できない回
    // (Episode が null)は、再適用も揃えられない(対象外にする)ので出さない。
    private static List<SettingsDriftGroup> Drift(IEnumerable<ResolvedLedgerEntry> resolved) =>
        resolved
            .Where(r => r.Episode is not null && !r.IsAsRecorded)
            .GroupBy(r => (r.Entry.Station, r.Entry.Program, ToStation: r.Episode!.Station, ToProgram: r.Episode.Program))
            .Select(g => new SettingsDriftGroup(g.Key.Station, g.Key.Program, g.Key.ToStation, g.Key.ToProgram, g.Count()))
            .OrderBy(g => g.Station, StringComparer.Ordinal)
            .ThenBy(g => g.FromAlbum, StringComparer.Ordinal)
            .ThenBy(g => g.ToStation, StringComparer.Ordinal)
            .ThenBy(g => g.ToAlbum, StringComparer.Ordinal)
            .ToList();
}
