using System.IO.Abstractions;
using Radirec.Core.Configuration;
using Radirec.Core.Ingestion;
using Radirec.Core.Jellyfin;
using Radirec.Core.Ledger;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Output;
using Radirec.Core.Reapply;
using Radirec.Core.Sources;

namespace Radirec.Tests;

/// <summary>
/// 統合設定の再適用(ADR-0016)を実ファイル・実台帳(SQLite)・実 TagLib で通す統合テスト。
/// 取り込み済みの状態は、統合設定の無い設定で実際に取り込んで作る。
/// </summary>
public sealed class ReapplyServiceTests : IDisposable
{
    private const string Station = "TBSラジオ";
    private const string Folder1 = "パンサー向井のふらっと(1)";
    private const string Folder2 = "パンサー向井のふらっと(2)";
    private const string Album = "パンサー向井のふらっと";

    private const string Dec23At0830 = "20241223083000";
    private const string Dec30At0830 = "20241230083000";
    private const string Dec23At1000 = "20241223100000";

    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "radirec-test-" + Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _ledgers = [];

    public ReapplyServiceTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        foreach (var ledger in _ledgers)
            ledger.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_work))
            Directory.Delete(_work, recursive: true);
    }

    private string RadioRoot => Path.Combine(_work, "radio");
    private string OutputRoot => Path.Combine(_work, "out");

    private string OutputOf(string folder, string date, int? part = null, string station = Station)
        => Path.Combine(OutputRoot, station, folder, $"{folder} {date}{(part is int p ? $"-{p}" : "")}.m4a");

    private static ProgramMasterConfig NoPrograms() => new();

    private static ProgramMasterConfig Merged(string station = Station, string album = Album, params string[] folders)
        => new()
        {
            Programs =
            [
                new ProgramAlias
                {
                    Match = new ProgramMatch
                    {
                        Station = station,
                        Folders = folders.Length > 0 ? [.. folders] : [Folder1, Folder2],
                    },
                    Canonical = new ProgramCanonical { Album = album },
                },
            ],
        };

    // 無音フィクスチャを らじれこ風の {局}/{番組}/ に置く(ファイル名の番組部は識別に使わないので表示用)。
    private string PlaceSource(string folder, string stamp, string station = Station, string? program = null)
    {
        var dir = Path.Combine(RadioRoot, station, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"[{station}]{program ?? Album}(TimeFree)_{stamp}.m4a");
        File.Copy(Fixture, path);
        return path;
    }

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "silent.m4a");

    private static void CopyWithDifferentAudio(string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(Fixture, to);
        long middle;
        using (var file = TagLib.File.Create(to))
            middle = (file.InvariantStartPosition + file.InvariantEndPosition) / 2;
        var bytes = File.ReadAllBytes(to);
        bytes[middle] ^= 0xFF;
        File.WriteAllBytes(to, bytes);
    }

    private SqliteLedger Ledger(string name = "ledger")
    {
        var ledger = new SqliteLedger(Path.Combine(_work, name + ".db"));
        _ledgers.Add(ledger);
        return ledger;
    }

    private IngestService Ingest(
        ILedger ledger, ProgramMasterConfig programs, string? outputRoot = null, StationMasterConfig? stations = null)
    {
        var fileSystem = new FileSystem();
        var tagReader = new TagLibTagReader();
        return new IngestService(
            new FileSystemRecordingSource(fileSystem, RadioRoot),
            new SourceRouter([new RajirekoImporter(), new RadikoolImporter(tagReader)]),
            new StabilityChecker(TimeProvider.System, TimeSpan.Zero),
            new RecordingSkipPolicy([]),
            tagReader,
            new EpisodeAssembler(
                new StationResolver(stations ?? new StationMasterConfig()),
                new ProgramResolver(programs)),
            new LocalFolderTarget(fileSystem, new TagLibTagWriter(), new TagLibAudioContentComparer(), outputRoot ?? OutputRoot),
            ledger,
            TimeProvider.System,
            genre: "ラジオ",
            new NullLibraryRefresher(),
            new NullIngestSummaryPublisher(),
            new NullIngestFailureStore(),
            new NullIngestFailurePublisher());
    }

    private ReapplyService Reapply(
        ILedger ledger, ProgramMasterConfig programs, StationMasterConfig? stations = null, ILibraryRefresher? refresher = null,
        ITagWriter? tagWriter = null)
    {
        var fileSystem = new FileSystem();
        var comparer = new TagLibAudioContentComparer();
        return new ReapplyService(
            fileSystem,
            ledger,
            new RajirekoImporter(),
            new StationResolver(stations ?? new StationMasterConfig()),
            new ProgramResolver(programs),
            new LocalFolderTarget(fileSystem, tagWriter ?? new TagLibTagWriter(), comparer, OutputRoot),
            new TagLibTagReader(),
            comparer,
            refresher ?? new NullLibraryRefresher(),
            programs.KeepSeparate);
    }

    // (1) の 1 回だけを統合設定の無いまま取り込み、統合先の名前へファイルを mv しただけ(タグは (1) のまま)の状態を作る。
    // 返すのは台帳と、今の設定での場所(mv 先)。
    private async Task<(SqliteLedger Ledger, string Target)> IngestOneAndRenameByHandAsync()
    {
        PlaceSource(Folder1, Dec23At0830);
        var ledger = Ledger();
        await Ingest(ledger, NoPrograms()).RunOnceAsync(dryRun: false);
        var target = OutputOf(Album, "2024-12-23", part: 1);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(OutputOf(Folder1, "2024-12-23"), target);
        return (ledger, target);
    }

    // (1) に 2 回、(2) に 1 回を、統合設定の無いまま取り込んだ状態を作る。
    private async Task<SqliteLedger> IngestSplitAsync()
    {
        PlaceSource(Folder1, Dec23At0830);
        PlaceSource(Folder1, Dec30At0830);
        PlaceSource(Folder2, Dec23At1000);
        var ledger = Ledger();
        var report = await Ingest(ledger, NoPrograms()).RunOnceAsync(dryRun: false);
        Assert.Equal(3, report.Count(IngestOutcome.Imported));
        return ledger;
    }

    private static (string? Album, string? Title, uint Track, string? Comment, string? Genre, uint Disc, string? Artist) TagsOf(string path)
    {
        using var file = TagLib.File.Create(path);
        var t = file.Tag;
        return (t.Album, t.Title, t.Track, t.Comment, t.FirstGenre, t.Disc, t.FirstPerformer);
    }

    private static string[] FilesUnder(string root) => Directory.Exists(root)
        ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p)).Order(StringComparer.Ordinal).ToArray()
        : [];

    // ---- 要対応の「設定と食い違った回」との一致(#172)----

    [Fact]
    public async Task AttentionDrift_counts_the_same_episodes_that_reapply_would_align()
    {
        // 要対応の食い違いは、再適用の「記録どおりか」の判定を共有する。同じ台帳・同じ設定で、再適用が揃える回
        // (対象外を除く)と、要対応が数える回が一致すること。揃えた後にパートの順だけを入れ替えると、アルバムは同じで
        // パートだけが食い違う。
        var ledger = await IngestSplitAsync();
        await Reapply(ledger, Merged()).RunAsync(dryRun: false); // いったん揃える
        var reordered = Merged(folders: [Folder2, Folder1]);    // パートの順だけを入れ替える

        var plan = await Reapply(ledger, reordered).RunAsync(dryRun: true);
        var resolver = new LedgerEpisodeResolver(
            new FileSystem().Path, new RajirekoImporter(),
            new StationResolver(new StationMasterConfig()), new ProgramResolver(reordered));
        var attention = Radirec.Core.Attention.AttentionCalculator.Compute(
            ledger.ListAll(), resolver, new Radirec.Core.Attention.AttentionPrograms(reordered));

        var aligned = plan.Items.Count(i => i.Action != ReapplyAction.Excluded);
        Assert.True(aligned > 0);
        Assert.Equal(aligned, attention.SettingsDrift.Sum(d => d.Count));
    }

    [Fact]
    public async Task AttentionDrift_leaves_out_station_changes_as_reapply_does()
    {
        // stations.json で局の正規名を変えた。再適用は局の変更を対象外にして揃えない(ADR-0016)。要対応の食い違いも、
        // 局が変わる回を数えない(再適用で解消できないため。#156)。両者の数は一致する(ここでは 0)。
        var ledger = await IngestSplitAsync();
        var stations = new StationMasterConfig
        {
            Stations = [new StationAlias { Canonical = "TBS", Aliases = [Station] }],
        };
        var programs = Merged(station: "TBS");

        var plan = await Reapply(ledger, programs, stations).RunAsync(dryRun: true);
        var resolver = new LedgerEpisodeResolver(
            new FileSystem().Path, new RajirekoImporter(), new StationResolver(stations), new ProgramResolver(programs));
        var attention = Radirec.Core.Attention.AttentionCalculator.Compute(
            ledger.ListAll(), resolver, new Radirec.Core.Attention.AttentionPrograms(programs));

        Assert.NotEmpty(plan.Items);
        Assert.All(plan.Items, i => Assert.Equal(ReapplyAction.Excluded, i.Action));
        Assert.Equal(plan.Items.Count(i => i.Action != ReapplyAction.Excluded), attention.SettingsDrift.Sum(d => d.Count));
        Assert.Empty(attention.SettingsDrift);
    }

    // ---- 確認した予定と実行直前の予定の照合(トレイ #126)----

    [Fact]
    public async Task SamePlan_WhenNothingChangedSinceConfirmation()
    {
        var ledger = await IngestSplitAsync();
        var programs = Merged();

        var confirmed = await Reapply(ledger, programs).RunAsync(dryRun: true);
        var replanned = await Reapply(ledger, programs).RunAsync(dryRun: true);

        Assert.True(replanned.HasSamePlanAs(confirmed));
    }

    [Fact]
    public async Task DifferentPlan_WhenProgramsChangedSinceConfirmation()
    {
        // 確認ダイアログを開いている間に programs.json の統合先が書き換わった場合。
        var ledger = await IngestSplitAsync();

        var confirmed = await Reapply(ledger, Merged()).RunAsync(dryRun: true);
        var replanned = await Reapply(ledger, Merged(album: "別の統合先")).RunAsync(dryRun: true);

        Assert.False(replanned.HasSamePlanAs(confirmed));
    }

    [Fact]
    public async Task DifferentPlan_WhenTargetAppearedSinceConfirmation()
    {
        // 確認の後に、移動先へ違う内容のファイルが現れた(移動 → 衝突のため対象外 に変わる)。
        var ledger = await IngestSplitAsync();
        var programs = Merged();

        var confirmed = await Reapply(ledger, programs).RunAsync(dryRun: true);
        CopyWithDifferentAudio(OutputOf(Album, "2024-12-23", part: 1));
        var replanned = await Reapply(ledger, programs).RunAsync(dryRun: true);

        Assert.False(replanned.HasSamePlanAs(confirmed));
    }

    [Fact]
    public async Task DryRun_ShowsMovesPerAlbum_AndChangesNothing()
    {
        var ledger = await IngestSplitAsync();
        var filesBefore = FilesUnder(OutputRoot);
        var ledgerBefore = ledger.ListAll();

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: true);

        Assert.True(report.DryRun);
        Assert.All(report.Items, i => Assert.Equal(ReapplyAction.Move, i.Action));
        Assert.Equal(
            [
                new ReapplyAlbumSummary(Station, Folder1, Album, Moves: 2, 0, 0, 0, 0, 0),
                new ReapplyAlbumSummary(Station, Folder2, Album, Moves: 1, 0, 0, 0, 0, 0),
            ],
            report.Albums);
        Assert.Contains(report.Items, i => i.ToPath == OutputOf(Album, "2024-12-23", part: 1));
        Assert.Equal(LibraryRefreshStatus.NotNeeded, report.LibraryRefresh);

        Assert.Equal(filesBefore, FilesUnder(OutputRoot));
        Assert.Equal(ledgerBefore, ledger.ListAll());
    }

    [Fact]
    public async Task Apply_Merge_MatchesAFreshIngest_AndKeepsOtherTags()
    {
        var ledger = await IngestSplitAsync();

        // 利用者が取り込み後に直したタグ(出演者)は再適用で消えない。
        using (var file = TagLib.File.Create(OutputOf(Folder1, "2024-12-23")))
        {
            file.Tag.Performers = ["ゲスト"];
            file.Save();
        }

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: false);

        Assert.Equal(3, report.Count(ReapplyAction.Move));
        Assert.Equal(3, report.Items.Count);

        // 同じソースを統合設定ありで新しく取り込んだ結果と、ファイル名・フォルダ・album / title / track が同じ。
        var freshRoot = Path.Combine(_work, "fresh");
        await Ingest(Ledger("fresh"), Merged(), freshRoot).RunOnceAsync(dryRun: false);
        Assert.Equal(FilesUnder(freshRoot), FilesUnder(OutputRoot));
        Assert.Equal(
            [
                Path.Combine(Station, Album, $"{Album} 2024-12-23-1.m4a"),
                Path.Combine(Station, Album, $"{Album} 2024-12-23-2.m4a"),
                Path.Combine(Station, Album, $"{Album} 2024-12-30-1.m4a"),
            ],
            FilesUnder(OutputRoot));
        foreach (var relative in FilesUnder(OutputRoot))
        {
            var moved = TagsOf(Path.Combine(OutputRoot, relative));
            var fresh = TagsOf(Path.Combine(freshRoot, relative));
            Assert.Equal((fresh.Album, fresh.Title, fresh.Track), (moved.Album, moved.Title, moved.Track));
            // album / title / track 以外は取り込み時のまま。
            Assert.Equal((fresh.Comment, fresh.Genre, fresh.Disc), (moved.Comment, moved.Genre, moved.Disc));
        }

        var part1 = TagsOf(OutputOf(Album, "2024-12-23", part: 1));
        Assert.Equal((Album, "2024-12-23 (1)", 12231u), (part1.Album, part1.Title, part1.Track));
        Assert.Equal("ゲスト", part1.Artist);

        // 空になった移動元フォルダは消える。
        Assert.False(Directory.Exists(Path.Combine(OutputRoot, Station, Folder1)));
        Assert.False(Directory.Exists(Path.Combine(OutputRoot, Station, Folder2)));
        Assert.Empty(Directory.GetFiles(OutputRoot, "*.partial.*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Apply_RewritesLedger_SoARescanDoesNotReimport_AndASecondRunDoesNothing()
    {
        var ledger = await IngestSplitAsync();
        var dec23 = new DateOnly(2024, 12, 23);

        await Reapply(ledger, Merged()).RunAsync(dryRun: false);

        var rows = ledger.ListAll().OrderBy(e => e.BroadcastDate).ThenBy(e => e.BroadcastTime).ToList();
        Assert.All(rows, r => Assert.Equal(Album, r.Program));
        Assert.Equal([1, 2, 1], rows.Select(r => r.PartIndex));
        Assert.Equal(
            [OutputOf(Album, "2024-12-23", 1), OutputOf(Album, "2024-12-23", 2), OutputOf(Album, "2024-12-30", 1)],
            rows.Select(r => r.OutputPath));

        // 同一性キーが書き換わっている(ソースパスや内容の関門だけで止まっているのではない)。
        Assert.True(ledger.Contains(new IdentityKey(Station, Album, dec23), new TimeOnly(8, 30), 1));
        Assert.True(ledger.Contains(new IdentityKey(Station, Album, dec23), new TimeOnly(10, 0), 2));
        Assert.False(ledger.Contains(new IdentityKey(Station, Folder1, dec23), new TimeOnly(8, 30), 0));
        Assert.False(ledger.Contains(new IdentityKey(Station, Folder2, dec23), new TimeOnly(10, 0), 0));

        // 統合設定のまま再スキャンしても取り込み直さない。
        var files = FilesUnder(OutputRoot);
        var rescan = await Ingest(ledger, Merged()).RunOnceAsync(dryRun: false);
        Assert.All(rescan.Items, i => Assert.Equal(IngestOutcome.SkippedAlreadyImported, i.Outcome));
        Assert.Equal(files, FilesUnder(OutputRoot));

        // 2 回目の再適用は何もしない(冪等)。
        var second = await Reapply(ledger, Merged()).RunAsync(dryRun: false);
        Assert.Empty(second.Items);
        Assert.False(second.HasChanges);
        Assert.True(second.IsAligned);
        Assert.Equal(LibraryRefreshStatus.NotNeeded, second.LibraryRefresh);
    }

    [Fact]
    public async Task Apply_Unmerge_MovesEpisodesBackToTheirFolders()
    {
        // 統合の解除も同じ仕組みで扱う。
        PlaceSource(Folder1, Dec23At0830);
        PlaceSource(Folder2, Dec23At1000);
        var ledger = Ledger();
        await Ingest(ledger, Merged()).RunOnceAsync(dryRun: false);

        var report = await Reapply(ledger, NoPrograms()).RunAsync(dryRun: false);

        Assert.Equal(2, report.Count(ReapplyAction.Move));
        Assert.Equal(
            [
                Path.Combine(Station, Folder1, $"{Folder1} 2024-12-23.m4a"),
                Path.Combine(Station, Folder2, $"{Folder2} 2024-12-23.m4a"),
            ],
            FilesUnder(OutputRoot));
        var tags = TagsOf(OutputOf(Folder1, "2024-12-23"));
        Assert.Equal((Folder1, "2024-12-23", 1223u), (tags.Album, tags.Title, tags.Track));
    }

    [Fact]
    public async Task FileAndTagsAlreadyAtNewPlace_OnlyTheLedgerIsFixed()
    {
        // 手作業で統合済みの回(記録の場所に無く、今の設定の場所にあり、タグも揃っている)。
        PlaceSource(Folder1, Dec23At0830);
        var ledger = Ledger();
        await Ingest(ledger, NoPrograms()).RunOnceAsync(dryRun: false);
        var original = OutputOf(Folder1, "2024-12-23");
        var target = OutputOf(Album, "2024-12-23", part: 1);
        await NewTarget().RelocateAsync(original, target, TagsFor(Album, 1));
        File.Delete(original);
        var bytes = File.ReadAllBytes(target);
        var refresher = new CountingRefresher();

        var report = await Reapply(ledger, Merged(), refresher: refresher).RunAsync(dryRun: false);

        var item = Assert.Single(report.Items);
        Assert.Equal(ReapplyAction.LedgerOnly, item.Action);
        Assert.Equal(bytes, File.ReadAllBytes(target)); // ファイルは動かさない・書き換えない
        Assert.Contains(ledger.ListAll(), e => e.OutputPath == target && e.PartIndex == 1 && e.Program == Album);
        Assert.Equal(0, refresher.Calls); // ファイルを変えていないので Jellyfin は更新しない
    }

    // ---- ファイル名だけ手で直した回(台帳とタグを直す。#141)----

    [Fact]
    public async Task FileRenamedByHand_TagsAndLedgerAreFixed_AndOtherTagsAreKept()
    {
        var (ledger, target) = await IngestOneAndRenameByHandAsync();
        using (var file = TagLib.File.Create(target))
        {
            file.Tag.Performers = ["ゲスト"];
            file.Save();
        }

        var before = TagsOf(target);
        Assert.Equal((Folder1, "2024-12-23", 1223u), (before.Album, before.Title, before.Track));
        var refresher = new CountingRefresher();

        var report = await Reapply(ledger, Merged(), refresher: refresher).RunAsync(dryRun: false);

        var item = Assert.Single(report.Items);
        Assert.Equal(ReapplyAction.LedgerAndTags, item.Action);
        Assert.Equal(target, item.ToPath);
        Assert.Equal(1, report.Count(ReapplyAction.LedgerAndTags));
        Assert.Equal(1, report.RewritesFiles);

        // album / title / track だけが新しく取り込んだ場合と同じになり、他のタグは残る。
        var after = TagsOf(target);
        Assert.Equal((Album, "2024-12-23 (1)", 12231u), (after.Album, after.Title, after.Track));
        Assert.Equal((before.Comment, before.Genre, before.Disc, "ゲスト"), (after.Comment, after.Genre, after.Disc, after.Artist));

        // ファイルは動かさない(同じ場所のまま)。.partial も残らない。
        Assert.Equal([Path.Combine(Station, Album, $"{Album} 2024-12-23-1.m4a")], FilesUnder(OutputRoot));
        Assert.Contains(ledger.ListAll(), e => e.OutputPath == target && e.PartIndex == 1 && e.Program == Album);

        // タグを書き換えたので Jellyfin の通常スキャンを起動する。
        Assert.Equal(1, refresher.Calls);
        Assert.Equal(LibraryRefreshStatus.Started, report.LibraryRefresh);

        // 2 回目は揃っている。
        var second = await Reapply(ledger, Merged()).RunAsync(dryRun: false);
        Assert.Empty(second.Items);
        Assert.True(second.IsAligned);
    }

    [Fact]
    public async Task FileRenamedByHand_DryRun_ShowsLedgerAndTags_AndChangesNothing()
    {
        var (ledger, target) = await IngestOneAndRenameByHandAsync();
        var bytes = File.ReadAllBytes(target);
        var ledgerBefore = ledger.ListAll();

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: true);

        Assert.Equal(ReapplyAction.LedgerAndTags, Assert.Single(report.Items).Action);
        Assert.Equal([new ReapplyAlbumSummary(Station, Folder1, Album, 0, LedgerAndTags: 1, 0, 0, 0, 0)], report.Albums);
        Assert.True(report.HasChanges);
        Assert.False(report.IsAligned);
        Assert.Equal(LibraryRefreshStatus.NotNeeded, report.LibraryRefresh);
        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.Equal(ledgerBefore, ledger.ListAll());
    }

    [Fact]
    public async Task FileRenamedByHand_TagRewriteFails_TheOriginalIsIntact_AndARerunFixesIt()
    {
        var (ledger, target) = await IngestOneAndRenameByHandAsync();
        var bytes = File.ReadAllBytes(target);
        var ledgerBefore = ledger.ListAll();

        // .partial にタグを書いている途中で止まった(書き込みに失敗した)。
        var report = await Reapply(ledger, Merged(), tagWriter: new FailingPlacementWriter()).RunAsync(dryRun: false);

        var item = Assert.Single(report.Items);
        Assert.Equal(ReapplyAction.Failed, item.Action);
        Assert.Equal(bytes, File.ReadAllBytes(target)); // 元のファイルは壊れない
        Assert.Equal([Path.Combine(Station, Album, $"{Album} 2024-12-23-1.m4a")], FilesUnder(OutputRoot)); // .partial も残らない
        Assert.Equal(ledgerBefore, ledger.ListAll()); // 台帳もそのまま

        var rerun = await Reapply(ledger, Merged()).RunAsync(dryRun: false);
        Assert.Equal(ReapplyAction.LedgerAndTags, Assert.Single(rerun.Items).Action);
        Assert.Equal(Album, TagsOf(target).Album);
    }

    [Fact]
    public async Task FileRenamedByHand_LeftoverPartialIsReplaced()
    {
        // 前回の書き換えが .partial の途中で止まり、書きかけが残っている。
        var (ledger, target) = await IngestOneAndRenameByHandAsync();
        var partial = Path.Combine(OutputRoot, Station, Album, $"{Album} 2024-12-23-1.partial.m4a");
        File.WriteAllBytes(partial, [1, 2, 3]);

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: false);

        Assert.Equal(ReapplyAction.LedgerAndTags, Assert.Single(report.Items).Action);
        Assert.False(File.Exists(partial));
        Assert.Equal(Album, TagsOf(target).Album);
    }

    [Fact]
    public async Task FileRenamedByHand_StoppedAfterTagCommitBeforeTheLedger_RerunFixesOnlyTheLedger()
    {
        // タグの確定の後・台帳を書き換える前に止まった状態:タグは揃っていて、台帳は古いまま。
        var (ledger, target) = await IngestOneAndRenameByHandAsync();
        await NewTarget().RewritePlacementAsync(target, TagsFor(Album, 1));
        var bytes = File.ReadAllBytes(target);

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: false);

        Assert.Equal(ReapplyAction.LedgerOnly, Assert.Single(report.Items).Action);
        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.Contains(ledger.ListAll(), e => e.OutputPath == target && e.Program == Album);
        Assert.Empty((await Reapply(ledger, Merged()).RunAsync(dryRun: false)).Items);
    }

    [Fact]
    public async Task DifferentPlan_WhenTagsWereFixedSinceConfirmation()
    {
        // 確認の後に、別の手段でタグが揃えられた(台帳とタグを直す → 台帳だけ直す に変わる)。
        var (ledger, target) = await IngestOneAndRenameByHandAsync();

        var confirmed = await Reapply(ledger, Merged()).RunAsync(dryRun: true);
        await NewTarget().RewritePlacementAsync(target, TagsFor(Album, 1));
        var replanned = await Reapply(ledger, Merged()).RunAsync(dryRun: true);

        Assert.Equal(ReapplyAction.LedgerAndTags, Assert.Single(confirmed.Items).Action);
        Assert.Equal(ReapplyAction.LedgerOnly, Assert.Single(replanned.Items).Action);
        Assert.False(replanned.HasSamePlanAs(confirmed));
    }

    [Fact]
    public async Task CopiedByHand_Duplicate_FixesTheTargetTags_AndKeepsTheOriginal()
    {
        // 手作業の統合で mv ではなく cp した回:統合先のコピーのタグは (1) のまま。台帳だけをそちらへ向けると、
        // 以後は記録どおりとして直らないので、移動先のタグも揃える(#141)。
        PlaceSource(Folder1, Dec23At0830);
        var ledger = Ledger();
        await Ingest(ledger, NoPrograms()).RunOnceAsync(dryRun: false);
        var original = OutputOf(Folder1, "2024-12-23");
        var target = OutputOf(Album, "2024-12-23", part: 1);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(original, target);
        var originalBytes = File.ReadAllBytes(original);
        var targetBytes = File.ReadAllBytes(target);
        var otherBefore = TagsOf(target);
        var refresher = new CountingRefresher();

        var dryRun = await Reapply(ledger, Merged()).RunAsync(dryRun: true);
        var planned = Assert.Single(dryRun.Items);
        Assert.Equal(ReapplyAction.DuplicateLeft, planned.Action);
        Assert.True(planned.RewritesTags);
        Assert.Contains("タグ", planned.Reason);
        Assert.Equal(1, dryRun.DuplicatesRewritingTags);
        Assert.Equal(targetBytes, File.ReadAllBytes(target)); // dry-run は書き換えない

        var report = await Reapply(ledger, Merged(), refresher: refresher).RunAsync(dryRun: false);

        var item = Assert.Single(report.Items);
        Assert.Equal(ReapplyAction.DuplicateLeft, item.Action);
        Assert.True(item.RewritesTags);
        Assert.Equal(1, report.RewritesFiles);
        var after = TagsOf(target);
        Assert.Equal((Album, "2024-12-23 (1)", 12231u), (after.Album, after.Title, after.Track));
        Assert.Equal((otherBefore.Comment, otherBefore.Genre, otherBefore.Disc, otherBefore.Artist),
            (after.Comment, after.Genre, after.Disc, after.Artist));
        Assert.Equal(originalBytes, File.ReadAllBytes(original)); // 元の場所のファイルは消さず、書き換えもしない
        Assert.Contains(ledger.ListAll(), e => e.OutputPath == target && e.Program == Album);
        Assert.Equal(1, refresher.Calls); // タグを書き換えたので Jellyfin を更新する
        Assert.Empty(Directory.GetFiles(OutputRoot, "*.partial.*", SearchOption.AllDirectories));

        Assert.Empty((await Reapply(ledger, Merged()).RunAsync(dryRun: false)).Items);
    }

    [Fact]
    public async Task FileAtNewPlaceWithUnreadableTags_IsExcluded_AndNothingChanges()
    {
        var (ledger, target) = await IngestOneAndRenameByHandAsync();
        File.WriteAllBytes(target, [1, 2, 3]); // 壊れていてタグを読めない
        var ledgerBefore = ledger.ListAll();

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: false);

        var item = Assert.Single(report.Items);
        Assert.Equal(ReapplyAction.Excluded, item.Action);
        Assert.Contains("タグを読み取れません", item.Reason);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.Equal(ledgerBefore, ledger.ListAll());
    }

    [Fact]
    public async Task SameAudioAtNewPlace_RepointsLedger_AndLeavesTheOriginal()
    {
        var ledger = await IngestSplitAsync();
        var original = OutputOf(Folder1, "2024-12-23");
        var target = OutputOf(Album, "2024-12-23", part: 1);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(original, target);

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: false);

        var item = Assert.Single(report.Items, i => i.ToPath == target);
        Assert.Equal(ReapplyAction.DuplicateLeft, item.Action);
        Assert.NotNull(item.Reason);
        Assert.True(File.Exists(original)); // 元の場所のファイルは消さない
        Assert.True(Directory.Exists(Path.Combine(OutputRoot, Station, Folder1))); // 残ったファイルがあるのでフォルダも残す
        Assert.Contains(ledger.ListAll(), e => e.OutputPath == target && e.Program == Album);
        Assert.Equal(1, report.Albums.Single(a => a.FromAlbum == Folder1).DuplicatesLeft);
    }

    [Fact]
    public async Task DifferentAudioAtNewPlace_IsExcluded_AndOthersProceed()
    {
        var ledger = await IngestSplitAsync();
        var original = OutputOf(Folder1, "2024-12-23");
        var target = OutputOf(Album, "2024-12-23", part: 1);
        CopyWithDifferentAudio(target);
        var originalBytes = File.ReadAllBytes(original);
        var targetBytes = File.ReadAllBytes(target);

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: false);

        var item = Assert.Single(report.Items, i => i.ToPath == target);
        Assert.Equal(ReapplyAction.Excluded, item.Action);
        Assert.Contains("衝突", item.Reason);
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
        Assert.Contains(ledger.ListAll(), e => e.OutputPath == original && e.Program == Folder1); // 台帳もそのまま
        Assert.Equal(2, report.Count(ReapplyAction.Move)); // 他の回は進める
        Assert.False(File.Exists(Path.Combine(OutputRoot, Station, Album, $"{Album} 2024-12-23-1 0830.m4a"))); // 別名で逃がさない
    }

    [Fact]
    public async Task StationChanged_IsExcluded_AndNothingMoves()
    {
        var ledger = await IngestSplitAsync();
        var files = FilesUnder(OutputRoot);
        var stations = new StationMasterConfig
        {
            Stations = [new StationAlias { Canonical = "TBS", Aliases = [Station] }],
        };

        var report = await Reapply(ledger, Merged(station: "TBS"), stations).RunAsync(dryRun: false);

        Assert.Equal(3, report.Items.Count);
        Assert.All(report.Items, i =>
        {
            Assert.Equal(ReapplyAction.Excluded, i.Action);
            Assert.Contains("放送局", i.Reason);
        });
        Assert.Equal(files, FilesUnder(OutputRoot));
        Assert.All(ledger.ListAll(), e => Assert.Equal(Station, e.Station));
    }

    [Fact]
    public async Task RadikoolEpisodes_AreNotTouched()
    {
        var ledger = Ledger();
        var radikool = new LedgerEntry
        {
            IdentityKey = new IdentityKey("ニッポン放送", "オードリーのオールナイトニッポン", new DateOnly(2020, 2, 23)).Value,
            Station = "ニッポン放送",
            Program = "オードリーのオールナイトニッポン",
            BroadcastDate = new DateOnly(2020, 2, 23),
            SourcePath = Path.Combine(_work, "radikool", "LFR", "オードリーのオールナイトニッポン_20200223.mp3"),
            OutputPath = Path.Combine(OutputRoot, "ニッポン放送", "オードリーのオールナイトニッポン", "x.mp3"),
            RuleVersion = NormalizationRuleVersion.Current,
            UsedFallback = false,
            ImportedAtUtc = DateTimeOffset.UnixEpoch,
        };
        ledger.Record(radikool);

        // 番組名の解決はフォルダ(LFR)ではなくファイル名由来なので、らじれこの規則で解決し直すと食い違う。それでも触らない。
        var report = await Reapply(ledger, NoPrograms()).RunAsync(dryRun: false);

        Assert.Empty(report.Items);
        Assert.Equal(radikool, Assert.Single(ledger.ListAll()));
    }

    [Fact]
    public async Task NewKeyAlreadyInLedger_IsExcluded()
    {
        var ledger = await IngestSplitAsync();
        // 同じ放送回が別のフォルダからも取り込まれて、統合後のキーの記録が既にある。
        var dec23 = new DateOnly(2024, 12, 23);
        ledger.Record(new LedgerEntry
        {
            IdentityKey = new IdentityKey(Station, Album, dec23).Value,
            Station = Station,
            Program = Album,
            BroadcastDate = dec23,
            BroadcastTime = new TimeOnly(8, 30),
            PartIndex = 1,
            SourcePath = "/elsewhere/a.m4a",
            OutputPath = "/elsewhere/out.m4a",
            RuleVersion = NormalizationRuleVersion.Current,
            UsedFallback = false,
            ImportedAtUtc = DateTimeOffset.UnixEpoch,
        });
        var original = OutputOf(Folder1, "2024-12-23");

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: false);

        var item = Assert.Single(report.Items, i => i.FromPath == original);
        Assert.Equal(ReapplyAction.Excluded, item.Action);
        Assert.Contains("台帳", item.Reason);
        Assert.True(File.Exists(original));
        Assert.Equal(4, ledger.ListAll().Count); // 記録は消えない
    }

    [Fact]
    public async Task StoppedAfterCommitBeforeDeletingTheOriginal_RerunRepointsLedger_AndKeepsTheOriginal()
    {
        // 確定(リネーム)の後・元を消す前に止まった状態:両方にファイルがあり、台帳は古いまま。
        var ledger = await IngestSplitAsync();
        var original = OutputOf(Folder1, "2024-12-23");
        var target = OutputOf(Album, "2024-12-23", part: 1);
        var originalBytes = File.ReadAllBytes(original);
        await NewTarget().RelocateAsync(original, target, TagsFor(Album, 1));

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: false);

        var item = Assert.Single(report.Items, i => i.ToPath == target);
        Assert.Equal(ReapplyAction.DuplicateLeft, item.Action);
        Assert.False(item.RewritesTags); // 移動先のタグは確定の前に書いてあるので揃っている
        Assert.Equal(originalBytes, File.ReadAllBytes(original)); // 元は壊れない
        Assert.Contains(ledger.ListAll(), e => e.OutputPath == target);
        Assert.Empty((await Reapply(ledger, Merged()).RunAsync(dryRun: false)).Items); // 揃った
    }

    [Fact]
    public async Task StoppedAfterDeletingTheOriginalBeforeTheLedger_RerunFixesOnlyTheLedger()
    {
        // 元を消した後・台帳を書き換える前に止まった状態:移動先にだけファイルがあり、台帳は古いまま。
        var ledger = await IngestSplitAsync();
        var original = OutputOf(Folder1, "2024-12-23");
        var target = OutputOf(Album, "2024-12-23", part: 1);
        await NewTarget().RelocateAsync(original, target, TagsFor(Album, 1));
        File.Delete(original);

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: false);

        Assert.Equal(ReapplyAction.LedgerOnly, Assert.Single(report.Items, i => i.ToPath == target).Action);
        Assert.Contains(ledger.ListAll(), e => e.OutputPath == target);
        Assert.Empty((await Reapply(ledger, Merged()).RunAsync(dryRun: false)).Items);
    }

    [Fact]
    public async Task StoppedBeforeCommit_LeftoverPartialIsReplaced_AndTheOriginalIsIntact()
    {
        // .partial を書いている途中で止まった状態:元はそのまま、移動先に書きかけの .partial が残っている。
        var ledger = await IngestSplitAsync();
        var original = OutputOf(Folder1, "2024-12-23");
        var originalBytes = File.ReadAllBytes(original);
        var partial = Path.Combine(OutputRoot, Station, Album, $"{Album} 2024-12-23-1.partial.m4a");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        File.WriteAllBytes(partial, [1, 2, 3]);
        Assert.Equal(originalBytes, File.ReadAllBytes(original));

        var report = await Reapply(ledger, Merged()).RunAsync(dryRun: false);

        Assert.Equal(3, report.Count(ReapplyAction.Move));
        Assert.False(File.Exists(partial));
        Assert.Equal(Album, TagsOf(OutputOf(Album, "2024-12-23", part: 1)).Album);
    }

    [Fact]
    public async Task MergeIntoOneOfTheSourceFolders_RenamesInPlace_AndKeepsThatFolder()
    {
        // 「垣花正あなたとハッピー！」と「…Part2」を前者の名前で統合する。前者の回は同じフォルダの中で -1 付きにリネームされる。
        const string station = "ニッポン放送";
        const string main = "垣花正あなたとハッピー！";
        const string part2 = "垣花正あなたとハッピー！Part2";
        PlaceSource(main, Dec23At0830, station, main);
        PlaceSource(part2, Dec23At1000, station, main);
        var ledger = Ledger();
        await Ingest(ledger, NoPrograms()).RunOnceAsync(dryRun: false);

        var report = await Reapply(ledger, Merged(station, main, main, part2)).RunAsync(dryRun: false);

        Assert.Equal(2, report.Count(ReapplyAction.Move));
        Assert.Equal(
            [
                Path.Combine(station, main, $"{main} 2024-12-23-1.m4a"),
                Path.Combine(station, main, $"{main} 2024-12-23-2.m4a"),
            ],
            FilesUnder(OutputRoot));
        Assert.False(Directory.Exists(Path.Combine(OutputRoot, station, part2)));
    }

    [Fact]
    public async Task CanonicalNameWhitespaceChange_KeepsTheIdentityKey_AndMoves()
    {
        const string station = "J-WAVE";
        const string folder = "LOGISTEEDRADIONOMICS";
        const string album = "LOGISTEED RADIONOMICS";
        PlaceSource(folder, Dec23At0830, station, album);
        var ledger = Ledger();
        await Ingest(ledger, NoPrograms()).RunOnceAsync(dryRun: false);

        var report = await Reapply(ledger, Merged(station, album, folder)).RunAsync(dryRun: false);

        Assert.Equal(ReapplyAction.Move, Assert.Single(report.Items).Action);
        var row = Assert.Single(ledger.ListAll());
        Assert.Equal(album, row.Program);
        Assert.Equal(0, row.PartIndex); // フォルダ 1 つの設定はパートを付けない
        Assert.Equal(OutputOf(album, "2024-12-23", station: station), row.OutputPath);
        Assert.True(File.Exists(row.OutputPath));
    }

    [Fact]
    public async Task CaseOnlyAlbumChange_IsExcluded_NotTreatedAsADuplicate()
    {
        // 正規名の大文字小文字だけを変えると、Windows では変更前と同じ物理ファイルになる。重複(台帳だけ向け直す)に
        // しない。大文字小文字を区別する FS(このテスト)でも同じ判定になる。
        const string station = "J-WAVE";
        const string folder = "LOGISTEEDRADIONOMICS";
        PlaceSource(folder, Dec23At0830, station, "Logisteed Radionomics");
        var ledger = Ledger();
        await Ingest(ledger, Merged(station, "Logisteed Radionomics", folder)).RunOnceAsync(dryRun: false);
        var before = Assert.Single(ledger.ListAll());
        var bytes = File.ReadAllBytes(before.OutputPath);

        var report = await Reapply(ledger, Merged(station, "LOGISTEED RADIONOMICS", folder)).RunAsync(dryRun: false);

        var item = Assert.Single(report.Items);
        Assert.Equal(ReapplyAction.Excluded, item.Action);
        Assert.Contains("大文字小文字", item.Reason);
        Assert.False(report.HasChanges);
        Assert.True(report.IsAligned); // 「揃っています」+ 対象外の一覧、と示す
        Assert.Equal(before, Assert.Single(ledger.ListAll())); // 台帳はそのまま
        Assert.Equal(bytes, File.ReadAllBytes(before.OutputPath)); // ファイルもそのまま
        Assert.Equal([Path.Combine(station, "Logisteed Radionomics", "Logisteed Radionomics 2024-12-23.m4a")], FilesUnder(OutputRoot));
    }

    [Fact]
    public async Task Report_ListsSiblingFoldersWithoutSettings_UntilTheyAreMerged()
    {
        var ledger = await IngestSplitAsync();

        var before = await Reapply(ledger, NoPrograms()).RunAsync(dryRun: true);
        var sibling = Assert.Single(before.UnintegratedSiblings);
        Assert.Equal(Station, sibling.Group.Station);
        Assert.Equal([Folder1, Folder2], sibling.Group.Folders);
        Assert.Equal([Folder1, Folder2], sibling.FoldersWithoutSetting);
        Assert.Empty(before.Items); // 設定を変えていないので動かす回は無い

        var after = await Reapply(ledger, Merged()).RunAsync(dryRun: true);
        Assert.Empty(after.UnintegratedSiblings);
    }

    [Fact]
    public async Task Report_DoesNotListSiblingFoldersKeptApart()
    {
        // 要対応と同じ計算(#155):分けておく組に記録した組は、再適用の確認にも出さない。
        var ledger = await IngestSplitAsync();
        var programs = NoPrograms();
        programs.KeepSeparate.Add(new KeptApartSiblings
        {
            Match = new ProgramMatch { Station = Station, Folders = [Folder1, Folder2] },
        });

        var report = await Reapply(ledger, programs).RunAsync(dryRun: true);

        Assert.Empty(report.UnintegratedSiblings);
        Assert.Empty(report.Items);
    }

    [Fact]
    public async Task Apply_WithMoves_RefreshesTheLibrary()
    {
        var ledger = await IngestSplitAsync();
        var refresher = new CountingRefresher();

        var dryRun = await Reapply(ledger, Merged(), refresher: refresher).RunAsync(dryRun: true);
        Assert.Equal(0, refresher.Calls);
        Assert.Equal(LibraryRefreshStatus.NotNeeded, dryRun.LibraryRefresh);

        var report = await Reapply(ledger, Merged(), refresher: refresher).RunAsync(dryRun: false);
        Assert.Equal(1, refresher.Calls);
        Assert.Equal(LibraryRefreshStatus.Started, report.LibraryRefresh);
    }

    private sealed class CountingRefresher : ILibraryRefresher
    {
        public int Calls { get; private set; }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    // 置き場所のタグ(.partial への書き込み)で失敗する。取り込みの全タグ書き込みは本物のまま。
    private sealed class FailingPlacementWriter : ITagWriter
    {
        public void Write(string filePath, NormalizedTags tags) => new TagLibTagWriter().Write(filePath, tags);

        public void WritePlacement(string filePath, NormalizedTags tags) =>
            throw new IOException("書き込みに失敗しました");
    }

    private LocalFolderTarget NewTarget()
        => new(new FileSystem(), new TagLibTagWriter(), new TagLibAudioContentComparer(), OutputRoot);

    private static NormalizedTags TagsFor(string album, int part) => TagMapper.Map(new Episode
    {
        Identity = new IdentityKey(Station, album, new DateOnly(2024, 12, 23)),
        PartIndex = part,
    });
}
