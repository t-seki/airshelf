using System.IO.Abstractions;
using Radirec.Core.Configuration;
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
/// 次の再適用で揃う(ADR-0003 と同じ考え方)。ファイルが既に今の設定の場所にある回は、タグ(<c>album</c> /
/// <c>title</c> / <c>track</c>)が違えば同じ場所で書き換えてから台帳を直し、揃っていれば台帳だけを直す(#141)。
/// 移動先に同じ内容のファイルがある回(重複)も、移動先のタグが違えば同じように書き換える。
/// ファイル名の番組名に「取り込まない」と判断した回(ADR-0021)は、出力先のファイル → 台帳の行 → 空になったフォルダの順に消す
/// (取込元は消さない)。「別の番組として取り込む」と判断した回は、解決したアルバムが変わるので、統合の変更と同じく動かす。
/// </remarks>
public sealed class ReapplyService(
    IFileSystem fileSystem,
    ILedger ledger,
    RajirekoImporter importer,
    IStationResolver stationResolver,
    IProgramResolver programResolver,
    LocalFolderTarget target,
    IPlacementTagReader tagReader,
    IAudioContentComparer contentComparer,
    ILibraryRefresher refresher,
    IEnumerable<KeptApartSiblings>? keepSeparate = null)
{
    // 台帳の回を今の設定で解決し直す(要対応と共有する。ファイルには触れない)。
    private readonly LedgerEpisodeResolver _episodes = new(fileSystem.Path, importer, stationResolver, programResolver);

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
            if (_episodes.Resolve(entry) is not { } resolved)
                continue;

            if (resolved.Episode is not { } episode)
            {
                items.Add(Item(entry, ReapplyAction.Excluded, entry.Program, entry.OutputPath, resolved.ParseError));
                continue;
            }

            var file = resolved.Source;
            knownFolders.Add(new ProgramFolder(episode.Station, file.ProgramFolder));

            // ファイル名の番組名に「取り込まない」と判断した回(ADR-0021)は、出力先から消す。記録どおりかの判定より前に見る
            // (アルバムはフォルダの番組のまま解決されるので、先に見ないと「別の番組」から変えた回を予約した番組のアルバムへ
            // 動かしてしまう。記録どおりの回も消す対象)。放送局が変わる回も消す(置き場所を決め直すわけではないので、
            // 放送局の変更を再適用しない理由が当てはまらない。要対応の「出力先から消す予定」と同じ回を扱う)。
            if (resolved.IsSkipDecided)
            {
                var removal = PlanRemoval(entry);
                if (!dryRun)
                    removal = ApplyRemoval(removal, entry, vacatedDirectories);
                items.Add(removal);
                continue;
            }

            // 記録どおり(アルバム名・パート・放送局が今の設定と同じ)の回は触らない。2 回目の再適用が何もしないのはこのため。
            if (resolved.IsAsRecorded)
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

            var item = Plan(entry, episode, updated, tags, claimedPaths, claimedKeys);
            if (item.Action is ReapplyAction.Move or ReapplyAction.LedgerAndTags or ReapplyAction.LedgerOnly
                or ReapplyAction.DuplicateLeft)
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

        var report = new ReapplyReport
        {
            Items = items,
            DryRun = dryRun,
            // 知っているフォルダ = 台帳にあるらじれこの回のフォルダ。要対応と同じ計算(分けておく組は出さない)。
            UnintegratedSiblings = Reapply.UnintegratedSiblings.Find(knownFolders, programResolver, keepSeparate ?? []),
        };

        // ファイル(置き場所かタグ)を書き換えた・消した回があれば、Jellyfin の通常スキャンを起動する。
        var (refreshStatus, refreshError) = dryRun || !report.ChangesLibrary
            ? (LibraryRefreshStatus.NotNeeded, null)
            : await LibraryRefresh.RunAsync(refresher, cancellationToken);

        return report with { LibraryRefresh = refreshStatus, RefreshError = refreshError };
    }

    // 1 回分をどう扱うかを、ファイルと台帳を見て決める(副作用なし。dry-run と実行で同じ)。
    private ReapplyItem Plan(
        LedgerEntry entry, Episode episode, LedgerEntry updated, NormalizedTags tags,
        HashSet<string> claimedPaths, HashSet<(string Key, TimeOnly? Time, int Part)> claimedKeys)
    {
        var item = PlanPlacement(entry, episode, updated, claimedPaths, claimedKeys);
        return item.Action is ReapplyAction.LedgerOnly or ReapplyAction.DuplicateLeft
            ? CheckTags(item, entry, episode, tags)
            : item;
    }

    // 台帳を今の設定の場所のファイルへ向ける回(台帳だけ直す候補・重複)だけ、そのファイルのタグを読む(全回のファイルは
    // 開かない)。手作業の統合でファイル名だけ直した回(mv)や、統合先へコピーした回(cp)は album / title / track が
    // 古いままなので、揃っていなければ同じ場所でタグを書き換える(#141)。台帳の記録が今の設定と揃っている回は
    // RunAsync が先に飛ばすので、ここには来ない(そのタグは確かめない)。
    private ReapplyItem CheckTags(ReapplyItem item, LedgerEntry entry, Episode episode, NormalizedTags tags)
    {
        PlacementTags current;
        try
        {
            current = tagReader.ReadPlacement(item.ToPath);
        }
        catch (Exception ex)
        {
            // 読めない(壊れている・他のアプリが開いている等)なら、タグも台帳も触らない。揃っているか分からない回の
            // 台帳だけを直すと、次の再適用で記録どおりとして見過ごされるため。
            return Item(entry, ReapplyAction.Excluded, episode.Program, item.ToPath, $"タグを読み取れません({ex.Message})");
        }

        var aligned = current.Album == tags.Album && current.Title == tags.Title && current.Track == tags.Track;
        if (aligned)
            return item;

        // 重複の回は区分を変えず(元の場所のファイルを残すことは変わらない)、移動先のタグも直すことを理由に添える。
        return item.Action == ReapplyAction.DuplicateLeft
            ? item with { RewritesTags = true, Reason = DuplicateReasonWithTags }
            : item with { Action = ReapplyAction.LedgerAndTags, RewritesTags = true };
    }

    // 「取り込まない」と判断した回の予定(副作用なし。dry-run と実行で同じ)。移動先は無いので、変更後のアルバム・場所は今のまま。
    // 出力ファイルが既に無ければ、台帳の行だけを消す(利用者が手で消した等)。
    private ReapplyItem PlanRemoval(LedgerEntry entry) =>
        fileSystem.File.Exists(entry.OutputPath)
            ? Item(entry, ReapplyAction.Remove, entry.Program, entry.OutputPath)
            : Item(entry, ReapplyAction.Remove, entry.Program, entry.OutputPath, RemoveLedgerOnlyReason);

    // 出力先のファイルを消してから台帳の行を消す(逆の順だと、ファイルを消せなかったときに台帳から見えないファイルが残る)。
    // ファイルを消した後・台帳の前に止まっても、次の再適用が「出力ファイルが無い回」として台帳の行だけを消す。取込元には触れない。
    private ReapplyItem ApplyRemoval(ReapplyItem item, LedgerEntry entry, HashSet<string> vacatedDirectories)
    {
        try
        {
            if (fileSystem.File.Exists(entry.OutputPath))
            {
                fileSystem.File.Delete(entry.OutputPath);
                vacatedDirectories.Add(fileSystem.Path.GetDirectoryName(entry.OutputPath)!);
            }

            if (!ledger.Remove(entry))
                return item with
                {
                    Action = ReapplyAction.Failed,
                    Reason = "台帳の記録を消せませんでした(記録が見つかりません)",
                };

            return item;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 消せなかった(他のアプリが開いている等)。ファイルも台帳も残り、次の再適用でもう一度消す。
            return item with { Action = ReapplyAction.Failed, Reason = ex.Message };
        }
    }

    private const string RemoveLedgerOnlyReason = "出力ファイルが見つかりません。台帳の記録だけを消します";

    private const string DuplicateReason ="移動先に同じ内容のファイルがあるため、元の場所のファイルを残しています";

    private const string DuplicateReasonWithTags =
        DuplicateReason + "。移動先のファイルのタグ(album / title / track)を直します";

    // 置き場所(移動・台帳だけ・重複・対象外)を決める。
    private ReapplyItem PlanPlacement(
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

        // 大文字小文字だけが違う(正規名の大文字小文字だけを変えた等)。本番(Windows)では同じ物理ファイルなので、
        // 「移動先にある・同じ音声」から重複と誤判定して台帳だけを向け直すと、ファイル名・タグは変わらないうえ、
        // 「重複は手で消す」に従うと唯一のファイルを消させてしまう。リネームの手順も別に要るので、いったん対象外にする。
        // 大文字小文字を区別するファイルシステムでも同じ判定にする(結果を OS で変えない)。
        if (!string.Equals(entry.OutputPath, newPath, StringComparison.Ordinal)
            && string.Equals(entry.OutputPath, newPath, StringComparison.OrdinalIgnoreCase))
            return Item(entry, ReapplyAction.Excluded, episode.Program, newPath,
                "変更前と大文字小文字だけが違う場所は対象外です(同じファイルになるため)");

        var currentExists = fileSystem.File.Exists(entry.OutputPath);

        // 置き場所が変わらない(アルバム名がファイル名に使えない文字だけ違う等)。台帳だけを直す。
        if (PathComparer.Equals(entry.OutputPath, newPath))
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
            ? Item(entry, ReapplyAction.DuplicateLeft, episode.Program, newPath, DuplicateReason)
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
            else if (item.RewritesTags)
            {
                // 台帳とタグを直す回・移動先のタグが古い重複の回。移動と同じく、ファイルを先に揃えてから台帳を書き換える。
                // 確定後・台帳の前に止まっても、次の再適用がタグの揃った回(台帳だけ直す・重複)として台帳を直す。
                // 重複の回でも元の場所のファイルには触れない。
                await target.RewritePlacementAsync(updated.OutputPath, tags, cancellationToken);
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
            // 1 回分の失敗で全体を止めない。元のファイルは確定前なら残り、確定後なら移動先にある(タグを直す回は
            // 同じ場所に、確定前なら元のタグのまま・確定後なら揃ったタグである)。いずれも次の再適用で揃う。
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
