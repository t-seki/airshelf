using Radirec.Core.Configuration;
using Radirec.Core.Ingestion;
using Radirec.Core.Jellyfin;
using Radirec.Core.Ledger;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Output;
using Radirec.Core.Sources;

namespace Radirec.Tests;

/// <summary>
/// 統合設定の無い分割番組らしいフォルダを、初めて取り込んだときに取り込み結果と取込要約シグナルへ載せる(#127)。
/// </summary>
public sealed class SiblingFolderNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private const string Bunka = "文化放送";
    private const string Tbs = "TBSラジオ";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeRecordingSource(IReadOnlyList<RecordingFile> files) : IRecordingSource
    {
        public IEnumerable<RecordingFile> Enumerate() => files;
    }

    // 記録をそのまま持つ台帳(「初めて取り込んだ」の判定に ListAll を使うため)。
    private sealed class MemoryLedger : ILedger
    {
        public List<LedgerEntry> Entries { get; } = [];
        public Exception? ThrowOnListAll { get; set; }

        public bool Contains(IdentityKey key, TimeOnly? broadcastTime, int partIndex) =>
            Entries.Any(e => e.IdentityKey == key.Value && e.BroadcastTime == broadcastTime && e.PartIndex == partIndex);

        public bool ContainsSource(string sourcePath) => Entries.Any(e => e.SourcePath == sourcePath);

        public bool ContainsContent(FileFingerprint fingerprint, DateOnly broadcastDate, TimeOnly? broadcastTime) => false;

        public void Record(LedgerEntry entry) => Entries.Add(entry);

        public bool Replace(LedgerEntry current, LedgerEntry updated) => throw new NotSupportedException();

        public IReadOnlyList<LedgerEntry> ListAll() => ThrowOnListAll is null ? Entries.ToList() : throw ThrowOnListAll;
    }

    private sealed class FakeOutputTarget : IOutputTarget
    {
        public OutputPlan Plan(RecordingFile s, Episode e, NormalizedTags t) =>
            new() { Path = $"/out/{e.Program}/{s.FileName}" };

        public Task<string> WriteAsync(RecordingFile s, OutputPlan plan, NormalizedTags t, CancellationToken ct = default)
            => Task.FromResult(plan.Path);

        public int CleanupOrphans() => 0;
    }

    private sealed class FakeTagReader : ITagReader
    {
        public string? ReadArtist(string filePath) => null;
        public RawTags ReadTags(string filePath) => new(Genre: null, Artist: null);
    }

    private sealed class FakePublisher : IIngestSummaryPublisher
    {
        public IngestSummary? Last { get; private set; }

        public Task PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default)
        {
            Last = summary;
            return Task.CompletedTask;
        }
    }

    private readonly MemoryLedger _ledger = new();
    private readonly FakePublisher _publisher = new();

    private static ProgramMasterConfig Programs(params (string Station, string Album, string[] Folders)[] merged) => new()
    {
        Programs = merged.Select(m => new ProgramAlias
        {
            Match = new ProgramMatch { Station = m.Station, Folders = [.. m.Folders] },
            Canonical = new ProgramCanonical { Album = m.Album },
        }).ToList(),
    };

    private IngestService Build(IReadOnlyList<RecordingFile> files, ProgramMasterConfig? programs = null)
    {
        programs ??= new ProgramMasterConfig();
        return new IngestService(
            new FakeRecordingSource(files),
            new RajirekoImporter(),
            new StabilityChecker(new FixedClock(Now), TimeSpan.FromSeconds(15)),
            new RecordingSkipPolicy([]),
            new FakeTagReader(),
            new EpisodeAssembler(new StationResolver(new StationMasterConfig()), new ProgramResolver(programs)),
            new SiblingFolderDetector(new ProgramResolver(programs), new RajirekoImporter()),
            new FakeOutputTarget(),
            _ledger,
            new FixedClock(Now),
            genre: "ラジオ",
            new NullLibraryRefresher(),
            _publisher,
            new NullIngestFailureStore(),
            new NullIngestFailurePublisher());
    }

    // らじれこの回({root}/{放送局}/{番組フォルダ}/[放送局]番組(TimeFree)_{日時}.m4a)。
    private static RecordingFile Rec(string station, string folder, string timestamp, string root = "/radio")
    {
        var name = $"[{station}]{folder}(TimeFree)_{timestamp}.m4a";
        return new RecordingFile
        {
            FilePath = $"{root}/{station}/{folder}/{name}",
            FileName = name,
            StationFolder = station,
            ProgramFolder = folder,
            Extension = ".m4a",
            SizeBytes = 1024,
            LastWriteTimeUtc = Now.AddMinutes(-1),
        };
    }

    private async Task<IngestReport> RunAsync(ProgramMasterConfig? programs, params RecordingFile[] files)
        => await Build(files, programs).RunOnceAsync(dryRun: false);

    [Fact]
    public async Task SecondPartFolder_FirstImport_WithoutSettings_IsNoticed()
    {
        // X(1) だけの時点では兄弟がいないので知らせない。
        var first = await RunAsync(null, Rec(Tbs, "爆笑問題の日曜サンデー (1)", "20260920130000"));
        Assert.Empty(first.SiblingFolderNotices);
        Assert.Empty(_publisher.Last!.SiblingFolderNotices);

        // X(2) が初めて取り込まれた時点で知らせる(兄弟も統合されていない文言)。
        var second = await RunAsync(null, Rec(Tbs, "爆笑問題の日曜サンデー (2)", "20260920150000"));

        var notice = Assert.Single(second.SiblingFolderNotices);
        Assert.Equal(Tbs, notice.Station);
        Assert.Equal("爆笑問題の日曜サンデー (2)", notice.Folder);
        Assert.Null(notice.MergedAlbum);
        Assert.Equal(["爆笑問題の日曜サンデー (1)", "爆笑問題の日曜サンデー (2)"], notice.Folders);
        // 取込要約シグナルにも同じものを載せる(提示は受け取る側で組み立てる)。
        Assert.Equal(second.SiblingFolderNotices, _publisher.Last!.SiblingFolderNotices);
    }

    [Fact]
    public async Task SiblingsInMergedProgram_NoticeCarriesMergedAlbum()
    {
        var programs = Programs((Bunka, "おとなりさん", ["おとなりさん 8時9時", "おとなりさん 9時10時", "おとなりさん 10時11時"]));
        await RunAsync(programs,
            Rec(Bunka, "おとなりさん 8時9時", "20260922080000"),
            Rec(Bunka, "おとなりさん 9時10時", "20260922090000"),
            Rec(Bunka, "おとなりさん 10時11時", "20260922100000"));

        var report = await RunAsync(programs, Rec(Bunka, "おとなりさん 11時12時", "20260923110000"));

        var notice = Assert.Single(report.SiblingFolderNotices);
        Assert.Equal("おとなりさん 11時12時", notice.Folder);
        Assert.Equal("おとなりさん", notice.MergedAlbum);
    }

    [Fact]
    public async Task SameFolder_SecondImport_IsNotNoticedAgain()
    {
        await RunAsync(null, Rec(Tbs, "X(1)", "20260913130000"));
        var first = await RunAsync(null, Rec(Tbs, "X(2)", "20260913150000"));
        Assert.Single(first.SiblingFolderNotices);

        // 同じフォルダの次の回。台帳にこのフォルダの回があるので知らせない。
        var second = await RunAsync(null, Rec(Tbs, "X(2)", "20260920150000"));

        Assert.Equal(1, second.Count(IngestOutcome.Imported));
        Assert.Empty(second.SiblingFolderNotices);
        Assert.Empty(_publisher.Last!.SiblingFolderNotices);
    }

    [Fact]
    public async Task SameScan_SeveralEpisodesOfSameFolder_NoticedOnce()
    {
        await RunAsync(null, Rec(Tbs, "X(1)", "20260906130000"));

        var report = await RunAsync(null,
            Rec(Tbs, "X(2)", "20260913150000"),
            Rec(Tbs, "X(2)", "20260920150000"));

        Assert.Equal(2, report.Count(IngestOutcome.Imported));
        Assert.Single(report.SiblingFolderNotices);
    }

    [Fact]
    public async Task SameScan_BothPartsFirstTime_NoticedOncePerGroup()
    {
        // 起動時キャッチアップで X(1) と X(2) が同じ走査で初めて入る。組単位の文言なので 1 件。
        var report = await RunAsync(null,
            Rec(Tbs, "X(1)", "20260920130000"),
            Rec(Tbs, "X(2)", "20260920150000"));

        var notice = Assert.Single(report.SiblingFolderNotices);
        Assert.Equal(["X(1)", "X(2)"], notice.Folders);
    }

    [Fact]
    public async Task LoneNumberedFolder_IsNotNoticed()
    {
        var report = await RunAsync(null, Rec("NHK第1", "ニューイヤー駅伝2024 (1)", "20240101083000"));

        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Empty(report.SiblingFolderNotices);
    }

    [Fact]
    public async Task FolderWithOwnSetting_IsNotNoticed()
    {
        // そのフォルダ自身に統合設定(Match.Folders)があれば、兄弟に設定が無くても知らせない。
        var programs = Programs((Tbs, "X", ["X(2)"]));
        await RunAsync(programs, Rec(Tbs, "X(1)", "20260920130000"));

        var report = await RunAsync(programs, Rec(Tbs, "X(2)", "20260920150000"));

        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Empty(report.SiblingFolderNotices);
    }

    [Fact]
    public async Task SpecialProgramTimeSlots_AreNotSiblings()
    {
        // 特番の「17時50分～19時」は末尾の「19時」だけが除かれ、残りの「17時50分～」が回ごとに違う。
        // 時間帯だけが違う同名の特番を既知として置いても兄弟にならない。
        await RunAsync(null, Rec(Bunka,
            "文化放送スポーツスペシャル プロ野球日本シリーズ 実況中継 17時50分～19時", "20221023175000"));

        var report = await RunAsync(null, Rec(Bunka,
            "文化放送スポーツスペシャル プロ野球日本シリーズ 実況中継 19時～21時", "20221030190000"));

        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Empty(report.SiblingFolderNotices);
    }

    [Fact]
    public async Task KnownFolderUnderAnotherRoot_IsNotFirstImport()
    {
        // 同じ放送局・同じフォルダ名なら、ソースの置き場所(organize-source・監視ルートの変更)が変わっても初回ではない。
        await RunAsync(null, Rec(Tbs, "X(1)", "20260906130000", root: "/old"));
        await RunAsync(null, Rec(Tbs, "X(2)", "20260906150000", root: "/old"));

        var report = await RunAsync(null, Rec(Tbs, "X(2)", "20260913150000", root: "/new"));

        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Empty(report.SiblingFolderNotices);
    }

    [Fact]
    public async Task DryRun_IsNotNoticed()
    {
        await RunAsync(null, Rec(Tbs, "X(1)", "20260920130000"));

        var report = await Build([Rec(Tbs, "X(2)", "20260920150000")]).RunOnceAsync(dryRun: true);

        Assert.Equal(1, report.Count(IngestOutcome.Planned));
        Assert.Empty(report.SiblingFolderNotices);
    }

    [Fact]
    public async Task LedgerUnreadable_ImportStillSucceeds_AndErrorIsReported()
    {
        await RunAsync(null, Rec(Tbs, "X(1)", "20260920130000"));
        _ledger.ThrowOnListAll = new InvalidOperationException("読めない");

        var report = await RunAsync(null, Rec(Tbs, "X(2)", "20260920150000"));

        // 判定を諦めるだけで、取り込みと取込要約シグナルは止めない。
        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.True(report.NotificationPublished);
        Assert.Empty(report.SiblingFolderNotices);
        Assert.Contains("読めない", report.SiblingFolderCheckError);
    }

    [Fact]
    public void Detector_IgnoresRadikoolEpisodes()
    {
        // radikool の回(括弧の無い MP3)は番組名がファイル名由来なので、兄弟の判定にも初回の判定にも使わない。
        var detector = new SiblingFolderDetector(new ProgramResolver(new ProgramMasterConfig()), new RajirekoImporter());
        var ledger = new[] { Entry(Tbs, "/radio/TBS/X(1)/X(1)_20200101.mp3") };

        Assert.Empty(detector.Detect(ledger, [(Tbs, "/radio/TBSラジオ/X(2)/[TBSラジオ]X(2)(TimeFree)_20260920150000.m4a")]));
        Assert.Empty(detector.Detect(
            [Entry(Tbs, "/radio/TBSラジオ/X(1)/[TBSラジオ]X(1)(TimeFree)_20260920130000.m4a")],
            [(Tbs, "/radio/TBS/X(2)/X(2)_20200101.mp3")]));
    }

    private static LedgerEntry Entry(string station, string sourcePath) => new()
    {
        IdentityKey = sourcePath,
        Station = station,
        Program = "p",
        BroadcastDate = new DateOnly(2020, 1, 1),
        SourcePath = sourcePath,
        OutputPath = "/out/p.m4a",
        RuleVersion = NormalizationRuleVersion.Current,
        UsedFallback = true,
        ImportedAtUtc = Now,
    };
}
