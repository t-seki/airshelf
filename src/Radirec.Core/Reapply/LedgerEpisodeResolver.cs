using System.IO.Abstractions;
using Radirec.Core.Ledger;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Sources;

namespace Radirec.Core.Reapply;

/// <summary>
/// 台帳のらじれこの回 1 件を、今の設定で解決し直したもの。台帳の <c>source_path</c> の名前だけから作る
/// (取込元・出力先のファイルには触れない)。
/// </summary>
/// <param name="Entry">台帳の記録。</param>
/// <param name="Source">取込元のパスから組んだ録音ファイル(ファイルが無くても名前だけで作る)。</param>
/// <param name="Episode">今の設定で解決した回。名前を解析できなかったら null(<see cref="ParseError"/>)。</param>
/// <param name="ParseError">名前を解析できなかった理由。解析できたら null。</param>
public sealed record ResolvedLedgerEntry(LedgerEntry Entry, RecordingFile Source, Episode? Episode, string? ParseError)
{
    /// <summary>
    /// 記録どおり(放送局・アルバム名・パートが今の設定と同じ)か。再適用が触らない回で、要対応の「設定と食い違った回」
    /// に出さない回。解析できなかった回は false。
    /// </summary>
    public bool IsAsRecorded => Episode is { } episode
        && episode.Station == Entry.Station
        && episode.Program == Entry.Program
        && (episode.PartIndex ?? 0) == Entry.PartIndex;

    /// <summary>
    /// ファイル名の番組名に「取り込まない」と判断された回か(ADR-0021)。再適用すれば出力先から消える回で、消すまでは要対応の
    /// 「出力先から消す予定」に出る。アルバムは変えない(<see cref="IsAsRecorded"/> には関わらない。再適用はこれを先に見る)。
    /// </summary>
    public bool IsSkipDecided => Episode?.FilenameVerdict == FilenameProgramVerdict.Skip;

    /// <summary>
    /// ファイル名の番組名がフォルダ名を含まず、判断も無い回か(差し替え放送の疑い。ADR-0021)。取り込み済みの判断待ちの回として
    /// 要対応に出す。アルバムはフォルダのまま解決する。
    /// </summary>
    public bool IsUnjudged => Episode?.FilenameVerdict == FilenameProgramVerdict.Unjudged;

    /// <summary>この回の番組フォルダ(今の設定で解決した放送局 + らじれこの番組フォルダ名)。解析できなかったら null。</summary>
    public ProgramFolder? Folder => Episode is { } episode ? new ProgramFolder(episode.Station, Source.ProgramFolder) : null;
}

/// <summary>
/// 台帳の回を、今の放送局・番組の設定で解決し直す(ADR-0016 の再適用と、ADR-0018 の要対応で共有する)。
/// 使うのはパスの文字列操作だけで、ファイルシステムには触れない(要対応は表示のたびに計算するので、NAS に触れない)。
/// </summary>
/// <remarks>対象はらじれこの回だけ。radikool は番組名がファイル名由来で、凍結したアーカイブなので対象外(ADR-0016)。</remarks>
public sealed class LedgerEpisodeResolver(
    IPath path, RajirekoImporter importer, IStationResolver stationResolver, IProgramResolver programResolver)
{
    private readonly EpisodeAssembler _assembler = new(stationResolver, programResolver);

    /// <summary>今の番組の設定の解決器(兄弟フォルダの「統合設定が無い」の判定にも使う)。</summary>
    public IProgramResolver ProgramResolver => programResolver;

    /// <summary>1 件を解決し直す。らじれこの回でなければ null。</summary>
    public ResolvedLedgerEntry? Resolve(LedgerEntry entry)
    {
        var file = SourceFileOf(entry.SourcePath);
        if (!importer.CanHandle(file))
            return null;

        try
        {
            return new ResolvedLedgerEntry(entry, file, _assembler.Assemble(importer.Parse(file)), null);
        }
        catch (SourceParseException ex)
        {
            return new ResolvedLedgerEntry(entry, file, null, ex.Message);
        }
    }

    /// <summary>らじれこの回だけを解決し直す(順序は <paramref name="entries"/> のまま)。</summary>
    public IEnumerable<ResolvedLedgerEntry> ResolveAll(IEnumerable<LedgerEntry> entries)
        => entries.Select(Resolve).OfType<ResolvedLedgerEntry>();

    // 台帳の source_path から、取り込みと同じ形の RecordingFile を組む(ファイルが無くても名前だけで判定できる)。
    private RecordingFile SourceFileOf(string sourcePath)
    {
        var programDir = path.GetDirectoryName(sourcePath);
        var stationDir = string.IsNullOrEmpty(programDir) ? null : path.GetDirectoryName(programDir);
        return new RecordingFile
        {
            FilePath = sourcePath,
            FileName = path.GetFileName(sourcePath),
            ProgramFolder = string.IsNullOrEmpty(programDir) ? string.Empty : path.GetFileName(programDir),
            StationFolder = string.IsNullOrEmpty(stationDir) ? string.Empty : path.GetFileName(stationDir),
            Extension = path.GetExtension(sourcePath).ToLowerInvariant(),
        };
    }
}
