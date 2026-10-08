using Airshelf.Core.Configuration;
using Airshelf.Core.Ledger;
using Airshelf.Core.Model;
using Airshelf.Core.Normalization;
using Airshelf.Core.Reapply;

namespace Airshelf.Core.Attention;

/// <summary>
/// 要対応(CONTEXT.md)の計算に使う番組の設定。
/// </summary>
/// <param name="Programs">
/// <c>programs.json</c> の中身。<c>airshelf.json</c> で programs.json を使わない設定(<c>ProgramsFile</c> が空)なら null。
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
    /// 再適用すれば消える(再適用しても衝突などで揃わない回は、実際に揃っていないので出し続ける)。今の設定で放送局が変わる回は、
    /// 再適用が揃えない(対象外。ADR-0016)ので含めない(#156)。
    /// </summary>
    public IReadOnlyList<SettingsDriftGroup> SettingsDrift { get; init; } = [];

    /// <summary>
    /// 差し替え放送の疑い(ADR-0021)。ファイル名の番組名がフォルダ名も番組のアルバム名も含まず、判断も無い回を、(局, フォルダ, ファイル名の番組名) の
    /// 組ごとにまとめたもの。保留した回(保留のストア)と、取り込み済みの判断待ちの回(台帳)の両方を数える。判断すれば消える。
    /// 並びは局・フォルダ・ファイル名の番組名の順。
    /// </summary>
    public IReadOnlyList<SubstituteSuspectGroup> SubstituteSuspects { get; init; } = [];

    /// <summary>
    /// 出力先から消す予定の回(ADR-0021)。ファイル名の番組名に「取り込まない」と判断された取り込み済みの回を、(局, 今のアルバム,
    /// フォルダ, ファイル名の番組名) ごとにまとめたもの。「番組の設定と食い違ったままの回」の一種で、再適用すれば消える。
    /// </summary>
    public IReadOnlyList<PendingRemovalGroup> PendingRemovals { get; init; } = [];

    /// <summary>
    /// 要対応の件数。兄弟フォルダの組・失敗・食い違いのまとまり・差し替え放送の疑いの組・消す予定のまとまりを 1 件ずつ数えた合計
    /// (ホームの見出し・<c>airshelf attention</c> の件数)。
    /// </summary>
    public int Count => UnintegratedSiblings.Count + UnresolvedFailures.Count + SettingsDrift.Count
        + SubstituteSuspects.Count + PendingRemovals.Count;
}

/// <summary>差し替え放送の疑いの 1 組(ADR-0021)。</summary>
/// <param name="Station">正規放送局。</param>
/// <param name="Folder">らじれこの番組フォルダ名。</param>
/// <param name="FilenameProgram">ファイル名の番組名。</param>
/// <param name="HeldCount">保留している回の数(まだ取り込んでいない)。</param>
/// <param name="ImportedCount">取り込み済みで判断を待っている回の数(フォルダの番組のアルバムに入っている)。</param>
public sealed record SubstituteSuspectGroup(string Station, string Folder, string FilenameProgram, int HeldCount, int ImportedCount);

/// <summary>出力先から消す予定の回の 1 まとまり(ADR-0021)。</summary>
/// <param name="Station">台帳に記録した放送局。</param>
/// <param name="Album">台帳に記録したアルバム名(今のアルバム)。</param>
/// <param name="Folder">らじれこの番組フォルダ名。</param>
/// <param name="FilenameProgram">ファイル名の番組名。</param>
/// <param name="Count">回数。</param>
public sealed record PendingRemovalGroup(string Station, string Album, string Folder, string FilenameProgram, int Count);

/// <summary>
/// 番組の設定と食い違ったままの回の 1 まとまり(W-135 の 1 行)。今の設定で放送局が変わる回は、再適用が揃えないので含めない
/// (<see cref="AttentionItems.SettingsDrift"/>)。
/// </summary>
/// <param name="Station">台帳に記録した放送局(今の置き場所の局。今の設定で解決した局も同じ)。</param>
/// <param name="FromAlbum">台帳に記録したアルバム名(今のアルバム)。</param>
/// <param name="ToAlbum">今の設定で解決したアルバム名(設定のアルバム)。パートだけが違う回は <paramref name="FromAlbum"/> と同じ。</param>
/// <param name="Count">この組の回数。</param>
public sealed record SettingsDriftGroup(string Station, string FromAlbum, string ToAlbum, int Count);

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
    /// <param name="held">保留のストアの全件(ADR-0021)。省略すると空。</param>
    public static AttentionItems Compute(
        IEnumerable<LedgerEntry> ledger, LedgerEpisodeResolver resolver, AttentionPrograms programs,
        IEnumerable<IngestFailureRecord>? failures = null, IEnumerable<HeldRecording>? held = null)
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
            SubstituteSuspects = Suspects(resolved, held ?? [], resolver.ProgramResolver),
            PendingRemovals = Removals(resolved),
        };
    }

    // 差し替え放送の疑い(ADR-0021)。保留のストアの回は、今の設定で判定し直す(判断を書いた後、次の走査がストアを片付けるまでの間も
    // 出し続けないため。いまの状態から導く。ADR-0018)。取り込み済みの回は、台帳の source_path から判定した「判断待ち」の回。
    // 組は照合キーでまとめ、表示には最初に出てきた表記を使う。
    private static List<SubstituteSuspectGroup> Suspects(
        IEnumerable<ResolvedLedgerEntry> resolved, IEnumerable<HeldRecording> held, IProgramResolver programResolver)
    {
        var groups = new Dictionary<(string, string, string), (string Station, string Folder, string Name, int Held, int Imported)>();

        void Add(string station, string folder, string name, int heldCount, int importedCount)
        {
            var key = (TextNormalizer.ToMatchKey(station), TextNormalizer.ToMatchKey(folder), TextNormalizer.ToMatchKey(name));
            groups[key] = groups.TryGetValue(key, out var g)
                ? g with { Held = g.Held + heldCount, Imported = g.Imported + importedCount }
                : (station, folder, name, heldCount, importedCount);
        }

        foreach (var h in held)
        {
            if (programResolver.JudgeFilenameProgram(h.Station, h.Folder, h.FilenameProgram).Verdict == FilenameProgramVerdict.Unjudged)
                Add(h.Station, h.Folder, h.FilenameProgram, 1, 0);
        }

        foreach (var r in resolved.Where(r => r.IsUnjudged))
            Add(r.Episode!.Station, r.Source.ProgramFolder, r.Episode.FilenameProgram ?? string.Empty, 0, 1);

        return groups.Values
            .Select(g => new SubstituteSuspectGroup(g.Station, g.Folder, g.Name, g.Held, g.Imported))
            .OrderBy(g => g.Station, StringComparer.Ordinal)
            .ThenBy(g => g.Folder, StringComparer.Ordinal)
            .ThenBy(g => g.FilenameProgram, StringComparer.Ordinal)
            .ToList();
    }

    // 「取り込まない」と判断された取り込み済みの回(再適用が出力先から消す。ADR-0021)。
    private static List<PendingRemovalGroup> Removals(IEnumerable<ResolvedLedgerEntry> resolved) =>
        resolved
            .Where(r => r.IsSkipDecided)
            .GroupBy(r => (r.Entry.Station, r.Entry.Program, r.Source.ProgramFolder, Name: r.Episode!.FilenameProgram ?? string.Empty))
            .Select(g => new PendingRemovalGroup(g.Key.Station, g.Key.Program, g.Key.ProgramFolder, g.Key.Name, g.Count()))
            .OrderBy(g => g.Station, StringComparer.Ordinal)
            .ThenBy(g => g.Album, StringComparer.Ordinal)
            .ThenBy(g => g.FilenameProgram, StringComparer.Ordinal)
            .ToList();

    // 記録どおりでない回(再適用が揃える回)を、局・今のアルバム → 設定のアルバムごとに数える。名前を解析できない回
    // (Episode が null)と、今の設定で放送局が変わる回(stations.json で正規名を変えた等)は、再適用が揃えない(対象外に
    // する。ADR-0016)ので出さない。再適用で解消できないものは、利用者の判断を待っているものではないため(#156)。
    // 局の変更を知らせるのは、再適用の確認の「対象外 N 回」。「取り込まない」と判断された回は、動かすのではなく消すので、
    // ここには出さず「出力先から消す予定」(Removals)に出す(ADR-0021)。
    private static List<SettingsDriftGroup> Drift(IEnumerable<ResolvedLedgerEntry> resolved) =>
        resolved
            .Where(r => r.Episode is not null && !r.IsAsRecorded && r.Episode.Station == r.Entry.Station && !r.IsSkipDecided)
            .GroupBy(r => (r.Entry.Station, r.Entry.Program, ToProgram: r.Episode!.Program))
            .Select(g => new SettingsDriftGroup(g.Key.Station, g.Key.Program, g.Key.ToProgram, g.Count()))
            .OrderBy(g => g.Station, StringComparer.Ordinal)
            .ThenBy(g => g.FromAlbum, StringComparer.Ordinal)
            .ThenBy(g => g.ToAlbum, StringComparer.Ordinal)
            .ToList();
}
