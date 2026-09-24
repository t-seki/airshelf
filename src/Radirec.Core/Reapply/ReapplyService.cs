using System.IO.Abstractions;
using Radirec.Core.Ingestion;
using Radirec.Core.Jellyfin;
using Radirec.Core.Ledger;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Output;
using Radirec.Core.Sources;

namespace Radirec.Core.Reapply;

/// <summary>
/// 統合設定の再適用(ADR-0016)。今の番組の設定(正規名・統合)を取り込み済みの回に当て直し、アルバムやパートが
/// ずれた回を、いま新しく取り込んだ場合と同じ形(フォルダ・ファイル名・<c>album</c> / <c>title</c> / <c>track</c>)に揃える。
/// ソースから取り込み直すのではなく、出力済みのファイルを動かし、台帳の記録を書き換える。
/// </summary>
/// <remarks>
/// 対象はらじれこの回だけ。台帳の <c>source_path</c> のフォルダ名を今の設定で解決し直し、記録された
/// アルバム名・パートと食い違う回を扱う。1 回分の手順は <c>.partial</c> にコピー → タグ書換 → 確定 → 元を消す →
/// 台帳を書き換える → 空になった移動元フォルダを消す、の順で、どこで止まっても元のファイルが先に壊れず、
/// 次の再適用で揃う(ADR-0003 と同じ考え方)。
/// </remarks>
public sealed class ReapplyService(
    IFileSystem fileSystem,
    ILedger ledger,
    RajirekoImporter importer,
    IStationResolver stationResolver,
    IProgramResolver programResolver,
    LocalFolderTarget target,
    IAudioContentComparer contentComparer,
    ILibraryRefresher refresher)
{
    private readonly EpisodeAssembler _assembler = new(stationResolver, programResolver);

    // 出力先の判定は本番(Windows)ではパスの大文字小文字を区別しない。
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<ReapplyReport> RunAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var items = new List<ReapplyItem>();
        var knownFolders = new List<ProgramFolder>();

        // この再適用の中で行き先になった場所と台帳の主キー。2 つの回が同じ場所・同じキーへ向かうのを防ぐ
        // (dry-run でも実行と同じ判定になるよう、ファイルや台帳を見るだけでなくここでも覚える)。
        var claimedPaths = new HashSet<string>(PathComparer);
        var claimedKeys = new HashSet<(string Key, TimeOnly? Time, int Part)>();
        var vacatedDirectories = new HashSet<string>(PathComparer);

        var entries = ledger.ListAll()
            .OrderBy(e => e.Station, StringComparer.Ordinal)
            .ThenBy(e => e.Program, StringComparer.Ordinal)
            .ThenBy(e => e.BroadcastDate)
            .ThenBy(e => e.BroadcastTime)
            .ThenBy(e => e.PartIndex)
            .ThenBy(e => e.SourcePath, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // らじれこの回だけ。radikool は番組名がファイル名由来で、凍結したアーカイブなので対象外(ADR-0016)。
            var file = SourceFileOf(entry.SourcePath);
            if (!importer.CanHandle(file))
                continue;

            Episode episode;
            try
            {
                episode = _assembler.Assemble(importer.Parse(file));
            }
            catch (SourceParseException ex)
            {
                items.Add(Item(entry, ReapplyAction.Excluded, entry.Program, entry.OutputPath, ex.Message));
                continue;
            }

            knownFolders.Add(new ProgramFolder(episode.Station, file.ProgramFolder));

            // 記録どおり(アルバム名・パート・放送局が今の設定と同じ)の回は触らない。2 回目の再適用が何もしないのはこのため。
            if (episode.Station == entry.Station
                && episode.Program == entry.Program
                && (episode.PartIndex ?? 0) == entry.PartIndex)
                continue;

            var tags = TagMapper.Map(episode);
            var newPath = target.PreferredPath(episode, tags, file.Extension);
            var updated = entry with
            {
                IdentityKey = episode.Identity.Value,
                Program = episode.Program,
                PartIndex = episode.PartIndex ?? 0,
                OutputPath = newPath,
            };

            var item = Plan(entry, episode, updated, claimedPaths, claimedKeys);
            if (item.Action is ReapplyAction.Move or ReapplyAction.LedgerOnly or ReapplyAction.DuplicateLeft)
            {
                claimedPaths.Add(newPath);
                claimedKeys.Add((updated.IdentityKey, updated.BroadcastTime, updated.PartIndex));
                if (!dryRun)
                    item = await ApplyAsync(item, entry, updated, tags, vacatedDirectories, cancellationToken);
            }

            items.Add(item);
        }

        if (!dryRun)
            RemoveEmptyDirectories(vacatedDirectories);

        var (refreshStatus, refreshError) = dryRun || !items.Any(i => i.Action == ReapplyAction.Move)
            ? (LibraryRefreshStatus.NotNeeded, null)
            : await LibraryRefresh.RunAsync(refresher, cancellationToken);

        return new ReapplyReport
        {
            Items = items,
            DryRun = dryRun,
            UnintegratedSiblings = UnintegratedSiblings(knownFolders),
            LibraryRefresh = refreshStatus,
            RefreshError = refreshError,
        };
    }

    // 1 回分をどう扱うかを、ファイルと台帳を見て決める(副作用なし。dry-run と実行で同じ)。
    private ReapplyItem Plan(
        LedgerEntry entry, Episode episode, LedgerEntry updated,
        HashSet<string> claimedPaths, HashSet<(string Key, TimeOnly? Time, int Part)> claimedKeys)
    {
        var newPath = updated.OutputPath;

        // stations.json の変更は再適用しない(ADR-0016)。提示するだけ。
        if (episode.Station != entry.Station)
            return Item(entry, ReapplyAction.Excluded, episode.Program, newPath,
                $"放送局が変わっています({entry.Station} → {episode.Station})。放送局の変更は再適用しません");

        // 新しい主キーが既に別の回で使われている(同じ放送が別のフォルダからも取り込まれている等)。
        // 台帳を書き換えると記録が 1 つ消えるので動かさない。主キーが変わらない回(正規名の空白だけの変更等)は自分自身なので除く。
        var newKey = (updated.IdentityKey, updated.BroadcastTime, updated.PartIndex);
        var sameKey = newKey == (entry.IdentityKey, entry.BroadcastTime, entry.PartIndex);
        if (!sameKey
            && (claimedKeys.Contains(newKey)
                || ledger.Contains(episode.Identity, updated.BroadcastTime, updated.PartIndex)))
            return Item(entry, ReapplyAction.Excluded, episode.Program, newPath,
                "台帳に同じ放送回の記録が既にあります");

        if (claimedPaths.Contains(newPath))
            return Item(entry, ReapplyAction.Excluded, episode.Program, newPath,
                "この再適用で別の回が同じ場所へ移ります");

        var currentExists = fileSystem.File.Exists(entry.OutputPath);

        // 置き場所が変わらない(アルバム名がファイル名に使えない文字だけ違う等)。台帳だけを直す。
        if (string.Equals(entry.OutputPath, newPath, StringComparison.Ordinal))
            return currentExists
                ? Item(entry, ReapplyAction.LedgerOnly, episode.Program, newPath)
                : Item(entry, ReapplyAction.Excluded, episode.Program, newPath, "出力ファイルが見つかりません");

        var targetExists = fileSystem.File.Exists(newPath);

        // 記録の場所に無く、今の設定の場所にある = 手作業で統合済み。ファイルは動かさず台帳だけを直す。
        if (!currentExists)
            return targetExists
                ? Item(entry, ReapplyAction.LedgerOnly, episode.Program, newPath)
                : Item(entry, ReapplyAction.Excluded, episode.Program, newPath, "出力ファイルが見つかりません");

        if (!targetExists)
            return Item(entry, ReapplyAction.Move, episode.Program, newPath);

        // 移動先に既にある。音声部分が同じなら台帳だけを向け直し、元の場所のファイルは消さない(ADR-0015 の比較)。
        // 違う内容なら動かさない。取り込みのように ` HHmm` の別名で逃がすことはしない(ADR-0016)。
        return contentComparer.HasSameAudio(entry.OutputPath, newPath)
            ? Item(entry, ReapplyAction.DuplicateLeft, episode.Program, newPath,
                "移動先に同じ内容のファイルがあるため、元の場所のファイルを残しています")
            : Item(entry, ReapplyAction.Excluded, episode.Program, newPath,
                "移動先に内容の違うファイルがあります(衝突のため対象外)");
    }

    private async Task<ReapplyItem> ApplyAsync(
        ReapplyItem item, LedgerEntry entry, LedgerEntry updated, NormalizedTags tags,
        HashSet<string> vacatedDirectories, CancellationToken cancellationToken)
    {
        try
        {
            if (item.Action == ReapplyAction.Move)
            {
                // 確定(リネーム)までは元のファイルに触れない。確定後に元を消し、最後に台帳を書き換える。
                // 確定後・台帳の前に止まっても、次の再適用が「移動先にある」回として台帳を直す。
                await target.RelocateAsync(entry.OutputPath, updated.OutputPath, tags, cancellationToken);
                fileSystem.File.Delete(entry.OutputPath);
                vacatedDirectories.Add(fileSystem.Path.GetDirectoryName(entry.OutputPath)!);
            }

            if (!ledger.Replace(entry, updated))
                return item with
                {
                    Action = ReapplyAction.Failed,
                    Reason = "台帳を書き換えられませんでした(記録が見つからないか、同じ放送回の記録が既にあります)",
                };

            return item;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 1 回分の失敗で全体を止めない。元のファイルは確定前なら残り、確定後なら移動先にある(次の再適用で揃う)。
            return item with { Action = ReapplyAction.Failed, Reason = ex.Message };
        }
    }

    // 移動で空になった番組フォルダを消す。何か残っていれば(他の回・利用者のファイル)消さない。
    private void RemoveEmptyDirectories(IEnumerable<string> directories)
    {
        foreach (var dir in directories)
        {
            try
            {
                if (fileSystem.Directory.Exists(dir) && !fileSystem.Directory.EnumerateFileSystemEntries(dir).Any())
                    fileSystem.Directory.Delete(dir);
            }
            catch
            {
                // 空フォルダが残るだけ(次の再適用では対象にならないので手で消す)。移動の成否は変えない。
            }
        }
    }

    // 統合設定の無いフォルダを含む兄弟フォルダの組。知っているフォルダ = 台帳にある らじれこの回のフォルダ。
    // 「統合設定が無い」はフォルダ単位(programs.json の Match.Folders に無い)で判定する(#127 と同じ基準)。
    private IReadOnlyList<UnintegratedSiblingGroup> UnintegratedSiblings(IEnumerable<ProgramFolder> knownFolders)
        => SiblingFolders.Find(knownFolders)
            .Select(g => new UnintegratedSiblingGroup(
                g, g.Folders.Where(f => programResolver.Resolve(g.Station, f).UsedFallback).ToList()))
            .Where(g => g.FoldersWithoutSetting.Count > 0)
            .ToList();

    // 台帳の source_path から、取り込みと同じ形の RecordingFile を組む(ファイルが無くても名前だけで判定できる)。
    private RecordingFile SourceFileOf(string sourcePath)
    {
        var programDir = fileSystem.Path.GetDirectoryName(sourcePath);
        var stationDir = string.IsNullOrEmpty(programDir) ? null : fileSystem.Path.GetDirectoryName(programDir);
        return new RecordingFile
        {
            FilePath = sourcePath,
            FileName = fileSystem.Path.GetFileName(sourcePath),
            ProgramFolder = string.IsNullOrEmpty(programDir) ? string.Empty : fileSystem.Path.GetFileName(programDir),
            StationFolder = string.IsNullOrEmpty(stationDir) ? string.Empty : fileSystem.Path.GetFileName(stationDir),
            Extension = fileSystem.Path.GetExtension(sourcePath).ToLowerInvariant(),
        };
    }

    private static ReapplyItem Item(
        LedgerEntry entry, ReapplyAction action, string toAlbum, string toPath, string? reason = null) => new()
    {
        Action = action,
        Station = entry.Station,
        FromAlbum = entry.Program,
        ToAlbum = toAlbum,
        BroadcastDate = entry.BroadcastDate,
        BroadcastTime = entry.BroadcastTime,
        FromPath = entry.OutputPath,
        ToPath = toPath,
        SourcePath = entry.SourcePath,
        Reason = reason,
    };
}
