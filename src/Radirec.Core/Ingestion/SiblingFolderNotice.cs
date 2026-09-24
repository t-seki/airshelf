using Radirec.Core.Ledger;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Sources;

namespace Radirec.Core.Ingestion;

/// <summary>
/// 初めて取り込んだ番組フォルダに統合設定が無く、同じ放送局に兄弟フォルダがある、という事実(#127)。
/// 取込要約シグナルと一緒に運ぶ。提示(取込通知の 1 行・<c>gui.log</c>)は受け取る側で組み立てる。
/// </summary>
public sealed record SiblingFolderNotice
{
    public required string Station { get; init; }

    /// <summary>初めて取り込んだフォルダ(らじれこの番組フォルダ名)。</summary>
    public required string Folder { get; init; }

    /// <summary>
    /// 兄弟フォルダが入っている統合番組のアルバム名。兄弟も統合されていなければ null
    /// (このときは組全体が統合設定の無いフォルダ)。
    /// </summary>
    public string? MergedAlbum { get; init; }

    /// <summary>兄弟フォルダの組(<see cref="Folder"/> を含む。序数順)。</summary>
    public required IReadOnlyList<string> Folders { get; init; }
}

/// <summary>
/// 取り込み走査で初めて取り込んだフォルダのうち、統合設定が要りそうなもの(<see cref="SiblingFolderNotice"/>)を見つける。
/// 兄弟フォルダの判定は再適用と同じ <see cref="SiblingFolders"/> を使い、知っているフォルダは台帳の
/// <c>source_path</c> から集める。対象はらじれこの回だけ(radikool は番組名がファイル名由来のため)。
/// </summary>
/// <remarks>
/// 「初めて」= その走査の前の台帳に、同じ放送局・同じフォルダ名(照合キーで比べる)から取り込んだ回が無い。
/// フォルダのフルパスでは比べない(<c>organize-source</c> やソースのリネームでパスが変わっても、既知のフォルダを
/// 初回と誤らないため)。知らせたかどうかは記憶しない。条件が「初めて取り込んだとき」なので 1 フォルダにつき 1 回になる。
/// </remarks>
public sealed class SiblingFolderDetector(IProgramResolver programResolver, RajirekoImporter rajireko)
{
    /// <param name="ledgerBeforeScan">走査の前の台帳の全記録。</param>
    /// <param name="restored">
    /// この走査で自己修復(ADR-0015)して台帳に書き戻した回。フォルダは既知として扱う(兄弟を探す対象に含め、
    /// そのフォルダ自体は「初めて取り込んだ」にしない)。
    /// </param>
    /// <param name="imported">この走査で取り込んだ回(正規名の放送局と取込元のパス)。</param>
    public IReadOnlyList<SiblingFolderNotice> Detect(
        IEnumerable<LedgerEntry> ledgerBeforeScan,
        IEnumerable<(string Station, string SourcePath)> restored,
        IEnumerable<(string Station, string SourcePath)> imported)
    {
        var known = ledgerBeforeScan
            .Select(e => (e.Station, e.SourcePath))
            .Concat(restored)
            .Select(e => FolderOf(e.Station, e.SourcePath))
            .OfType<ProgramFolder>()
            .ToList();
        var knownKeys = known.Select(Key).ToHashSet();

        // 同じ走査で同じフォルダの回が複数取り込まれても 1 つに数える。
        var importedFolders = imported
            .Select(i => FolderOf(i.Station, i.SourcePath))
            .OfType<ProgramFolder>()
            .DistinctBy(Key)
            .ToList();

        // 統合設定の有無はフォルダ単位(programs.json の Match.Folders に無い)で見る。再適用と同じ基準。
        // Episode.UsedFallback は放送局のフォールバックも含むので使わない。
        var firstTime = importedFolders
            .Where(f => !knownKeys.Contains(Key(f)) && programResolver.Resolve(f.Station, f.Folder).UsedFallback)
            .ToList();
        if (firstTime.Count == 0)
            return [];

        // 兄弟は走査前から知っているフォルダと、この走査で取り込んだフォルダの両方から探す
        // (起動時キャッチアップで X(1) と X(2) が同じ走査で初めて入る場合)。
        var groups = SiblingFolders.Find(known.Concat(importedFolders));

        var notices = new List<SiblingFolderNotice>();
        var reportedGroups = new HashSet<SiblingFolderGroup>(ReferenceEqualityComparer.Instance);
        foreach (var folder in firstTime)
        {
            var group = groups.FirstOrDefault(g => Contains(g, folder));
            if (group is null)
                continue;

            // 兄弟が統合番組に入っているなら、そのアルバム名を添える(複数あれば序数で最初のもの)。
            var mergedAlbum = group.Folders
                .Select(f => programResolver.Resolve(group.Station, f))
                .Where(r => !r.UsedFallback)
                .Select(r => r.Canonical)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();

            // 兄弟も統合されていないときの文言は組単位なので、同じ組を 2 度知らせない。
            if (mergedAlbum is null && !reportedGroups.Add(group))
                continue;

            notices.Add(new SiblingFolderNotice
            {
                Station = folder.Station,
                Folder = folder.Folder,
                MergedAlbum = mergedAlbum,
                Folders = group.Folders,
            });
        }

        return notices;
    }

    private static (string Station, string Folder) Key(ProgramFolder f)
        => (TextNormalizer.ToMatchKey(f.Station), TextNormalizer.ToMatchKey(f.Folder));

    private static bool Contains(SiblingFolderGroup group, ProgramFolder folder)
    {
        var (station, name) = Key(folder);
        return TextNormalizer.ToMatchKey(group.Station) == station
               && group.Folders.Any(f => TextNormalizer.ToMatchKey(f) == name);
    }

    // 取込元のパスから番組フォルダを採る。らじれこの回でなければ null(ファイルが無くても名前だけで判定できる)。
    private ProgramFolder? FolderOf(string station, string sourcePath)
    {
        var programDir = Path.GetDirectoryName(sourcePath);
        var folder = string.IsNullOrEmpty(programDir) ? string.Empty : Path.GetFileName(programDir);
        if (folder.Length == 0)
            return null;

        var file = new RecordingFile
        {
            FilePath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            ProgramFolder = folder,
            StationFolder = string.Empty,
            Extension = Path.GetExtension(sourcePath).ToLowerInvariant(),
        };
        return rajireko.CanHandle(file) ? new ProgramFolder(station, folder) : null;
    }
}
