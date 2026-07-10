using Radirec.Core.Configuration;
using Radirec.Core.Ingestion;
using Radirec.Core.Jellyfin;
using Radirec.Core.Ledger;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Output;
using Radirec.Core.Sources;

namespace Radirec.Tests;

public sealed class IngestServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 12, 23, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeRecordingSource(params RecordingFile[] files) : IRecordingSource
    {
        public IEnumerable<RecordingFile> Enumerate() => files;
    }

    private sealed class FakeLedger : ILedger
    {
        private readonly List<(string Key, TimeOnly? Time, int Part)> _entries = [];
        public HashSet<string> Sources { get; } = [];
        public IReadOnlyList<string> Keys => _entries.Select(e => e.Key).ToList();

        // 厳密一致(ADR-0004)。時刻 or part が異なれば別放送。
        public bool Contains(IdentityKey key, TimeOnly? broadcastTime, int partIndex) =>
            _entries.Any(e => e.Key == key.Value && e.Time == broadcastTime && e.Part == partIndex);

        public bool ContainsSource(string sourcePath) => Sources.Contains(sourcePath);

        public void Record(LedgerEntry entry)
        {
            _entries.Add((entry.IdentityKey, entry.BroadcastTime, entry.PartIndex));
            Sources.Add(entry.SourcePath);
        }
    }

    private sealed class FakeOutputTarget : IOutputTarget
    {
        public List<Episode> Written { get; } = [];
        public string PlanPath(RecordingFile s, Episode e, NormalizedTags t) => $"/out/{e.Program}.m4a";
        public Task<string> WriteAsync(RecordingFile s, Episode e, NormalizedTags t, CancellationToken ct = default)
        {
            Written.Add(e);
            return Task.FromResult($"/out/{e.Program}.m4a");
        }
        public int CleanupOrphans() => 0;
    }

    private sealed class FakeTagReader(string? artist = null) : ITagReader
    {
        public int Reads { get; private set; }

        public string? ReadArtist(string filePath)
        {
            Reads++;
            return artist;
        }

        public RawTags ReadTags(string filePath)
        {
            Reads++;
            return new RawTags(Genre: null, Artist: artist);
        }
    }

    private sealed class ThrowingOutputTarget : IOutputTarget
    {
        public string PlanPath(RecordingFile s, Episode e, NormalizedTags t) => "/out/plan.m4a";
        public Task<string> WriteAsync(RecordingFile s, Episode e, NormalizedTags t, CancellationToken ct = default)
            => throw new IOException("NAS 書き込み失敗(テスト)");
        public int CleanupOrphans() => 0;
    }

    private sealed class FakeRefresher : ILibraryRefresher
    {
        public int Calls { get; private set; }
        public Exception? Throws { get; set; }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Throws is null ? Task.CompletedTask : throw Throws;
        }
    }

    private sealed class FakePublisher : IIngestSummaryPublisher
    {
        public int Calls { get; private set; }
        public IngestSummary? Last { get; private set; }
        public Exception? Throws { get; set; }

        public Task PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = summary;
            return Throws is null ? Task.CompletedTask : throw Throws;
        }
    }

    private readonly FakeLedger _ledger = new();
    private readonly FakeOutputTarget _target = new();
    private readonly FakeRefresher _refresher = new();
    private readonly FakePublisher _publisher = new();
    private readonly FakeTagReader _tagReader = new("出演者");

    private IngestService Build(
        IEnumerable<RecordingFile> files, IEnumerable<string>? skipPatterns = null,
        IOutputTarget? target = null)
        => new(
            new FakeRecordingSource(files.ToArray()),
            new RajirekoImporter(),
            new StabilityChecker(new FixedClock(Now), TimeSpan.FromSeconds(15)),
            new RecordingSkipPolicy(skipPatterns ?? []),
            _tagReader,
            new EpisodeAssembler(
                new StationResolver(new StationMasterConfig()),
                new ProgramResolver(new ProgramMasterConfig())),
            target ?? _target,
            _ledger,
            new FixedClock(Now),
            genre: "ラジオ",
            _refresher,
            _publisher);

    private static RecordingFile Rec(
        string name, string station = "J-WAVE", string program = "LOGISTEEDRADIONOMICS",
        int ageSeconds = 60, long size = 1024)
        => new()
        {
            FilePath = "/in/" + name,
            FileName = name,
            StationFolder = station,
            ProgramFolder = program,
            Extension = Path.GetExtension(name).ToLowerInvariant(),
            SizeBytes = size,
            LastWriteTimeUtc = Now.AddSeconds(-ageSeconds),
        };

    private const string StandardName = "[J-WAVE]LOGISTEED RADIONOMICS(TimeFree)_20260612223000.m4a";

    [Fact]
    public async Task DryRun_NewFile_IsPlanned_NoWriteNoLedger()
    {
        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: true);

        Assert.True(report.DryRun);
        Assert.Equal(1, report.Count(IngestOutcome.Planned));
        Assert.Empty(_target.Written);
        Assert.Empty(_ledger.Keys);
    }

    [Fact]
    public async Task Run_NewFile_IsImported_AndRecorded()
    {
        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false);

        var item = Assert.Single(report.Items);
        Assert.Equal(IngestOutcome.Imported, item.Outcome);
        Assert.Single(_target.Written);
        Assert.True(_ledger.Contains(
            new IdentityKey("J-WAVE", "LOGISTEED RADIONOMICS", new DateOnly(2026, 6, 12)),
            broadcastTime: new TimeOnly(22, 30, 0), partIndex: 0));
    }

    [Fact]
    public async Task SameDate_DifferentTime_BothImported()
    {
        // 同一番組・同日で別時刻の2パート(パンサー向井のふらっと(1)/(2) 型)。両方取り込む。
        var files = new[]
        {
            Rec("[TBSラジオ]番組(TimeFree)_20240101083000.m4a", station: "TBSラジオ", program: "番組"),
            Rec("[TBSラジオ]番組(TimeFree)_20240101100000.m4a", station: "TBSラジオ", program: "番組"),
        };

        var report = await Build(files).RunOnceAsync(dryRun: false);

        Assert.Equal(2, report.Count(IngestOutcome.Imported));
        Assert.Equal(2, _target.Written.Count);
    }

    [Fact]
    public async Task Run_Twice_SecondRunIsIdempotentSkip()
    {
        var files = new[] { Rec(StandardName) };
        await Build(files).RunOnceAsync(dryRun: false);

        var second = await Build(files).RunOnceAsync(dryRun: false);

        Assert.Equal(1, second.Count(IngestOutcome.SkippedAlreadyImported));
        Assert.Single(_target.Written); // 2 回目は書き込まない
    }

    [Fact]
    public async Task Junk_MatchingSkipPattern_IsSkipped()
    {
        var junk = Rec("この時間はＮＨＫネットラジオではお聞き頂けません。_20241021200956.m4a",
            station: "NHKラジオ第1(radiko)", program: "番組");

        var report = await Build([junk], skipPatterns: ["この時間はＮＨＫネットラジオでは"]).RunOnceAsync(dryRun: false);

        Assert.Equal(1, report.Count(IngestOutcome.SkippedJunk));
        Assert.Empty(_target.Written);
    }

    [Fact]
    public async Task ParseFailure_IsReportedAsFailed()
    {
        var bad = Rec("[X局]番組(TimeFree)_20250229123000.m4a"); // 非閏年の2/29

        var report = await Build([bad]).RunOnceAsync(dryRun: false);

        Assert.Equal(1, report.Count(IngestOutcome.Failed));
    }

    [Fact]
    public async Task RecentlyModified_IsSkippedAsUnstable()
    {
        var report = await Build([Rec(StandardName, ageSeconds: 5)]).RunOnceAsync(dryRun: false);

        Assert.Equal(1, report.Count(IngestOutcome.SkippedUnstable));
    }

    [Fact]
    public async Task WriteFailure_IsReportedAsFailed_AndDoesNotAbortRun()
    {
        var files = new[]
        {
            Rec("[J-WAVE]番組A(TimeFree)_20260612223000.m4a", program: "AAA"),
            Rec("[J-WAVE]番組B(TimeFree)_20260613223000.m4a", program: "BBB"),
        };

        var report = await Build(files, target: new ThrowingOutputTarget()).RunOnceAsync(dryRun: false);

        // 1 件目が失敗しても 2 件目まで走査が続き、両方 Failed として報告される。
        Assert.Equal(2, report.Items.Count);
        Assert.Equal(2, report.Count(IngestOutcome.Failed));
        Assert.Empty(_ledger.Keys); // 失敗は台帳に記録しない
    }

    [Fact]
    public async Task AlreadyImported_SkipsWithoutReadingArtistTag()
    {
        var files = new[] { Rec(StandardName) };
        await Build(files).RunOnceAsync(dryRun: false); // 取込: 出演者を1回読む
        var afterImport = _tagReader.Reads;

        await Build(files).RunOnceAsync(dryRun: false); // 2回目: 既取込 → タグを読まずにスキップ

        Assert.Equal(1, afterImport);
        Assert.Equal(afterImport, _tagReader.Reads); // 追加読取なし(ADR-0003 の高速化)
    }

    [Fact]
    public async Task Run_WithImport_TriggersLibraryRefresh()
    {
        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false);

        Assert.Equal(1, _refresher.Calls);
        Assert.True(report.LibraryRefreshed);
        Assert.Null(report.RefreshError);
    }

    [Fact]
    public async Task DryRun_DoesNotRefresh()
    {
        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: true);

        Assert.Equal(0, _refresher.Calls);
        Assert.False(report.LibraryRefreshed);
    }

    [Fact]
    public async Task Run_NoImports_DoesNotRefresh()
    {
        // ジャンクのみ = 取込0件 → リフレッシュは起動しない。
        var junk = Rec("この時間はＮＨＫネットラジオでは_20241021200956.m4a");
        var report = await Build([junk], skipPatterns: ["この時間はＮＨＫネットラジオでは"]).RunOnceAsync(dryRun: false);

        Assert.Equal(0, _refresher.Calls);
        Assert.False(report.LibraryRefreshed);
    }

    [Fact]
    public async Task Run_RefreshFailure_DoesNotFailImport_ButReportsError()
    {
        _refresher.Throws = new JellyfinException("boom");

        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false);

        // 取り込み自体は成功(台帳記録済み)。更新失敗はエラーとして報告するが巻き戻さない。
        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Single(_ledger.Keys);
        Assert.False(report.LibraryRefreshed);
        Assert.Equal("boom", report.RefreshError);
    }

    [Fact]
    public async Task Run_RefreshTimeout_IsCaughtAsError_NotPropagated()
    {
        // HttpClient のタイムアウトは TaskCanceledException(OperationCanceledException のサブクラス)。
        // 外部キャンセルではないので、伝播させず失敗として握ること。
        _refresher.Throws = new TaskCanceledException("timeout");

        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false);

        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.False(report.LibraryRefreshed);
        Assert.Equal("timeout", report.RefreshError);
    }

    [Fact]
    public async Task Run_WithImport_PublishesSummary_WithProgramDateAndPart()
    {
        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false);

        Assert.Equal(1, _publisher.Calls);
        Assert.True(report.NotificationPublished);
        Assert.Null(report.NotificationError);

        Assert.NotNull(_publisher.Last);
        var summary = _publisher.Last!;
        Assert.Equal(1, summary.TotalImported);
        Assert.False(summary.Truncated);
        var album = Assert.Single(summary.Albums);
        Assert.Equal("J-WAVE", album.Station);
        // 番組名はフォルダ権威(ADR-0001)。Rec の既定 ProgramFolder = "LOGISTEEDRADIONOMICS"。
        Assert.Equal("LOGISTEEDRADIONOMICS", album.Program);
        var episode = Assert.Single(album.Episodes);
        Assert.Equal(new DateOnly(2026, 6, 12), episode.BroadcastDate);
        Assert.Equal(new TimeOnly(22, 30, 0), episode.BroadcastTime);
        Assert.Equal(0, episode.PartIndex);
    }

    [Fact]
    public async Task Run_MultipleEpisodesSameProgram_AreGroupedIntoOneAlbum()
    {
        // 同一番組・同日の別時刻2パート → 1 アルバムに 2 放送回。
        var files = new[]
        {
            Rec("[TBSラジオ]番組(TimeFree)_20240101083000.m4a", station: "TBSラジオ", program: "番組"),
            Rec("[TBSラジオ]番組(TimeFree)_20240101100000.m4a", station: "TBSラジオ", program: "番組"),
        };

        await Build(files).RunOnceAsync(dryRun: false);

        Assert.NotNull(_publisher.Last);
        var summary = _publisher.Last!;
        Assert.Equal(2, summary.TotalImported);
        var album = Assert.Single(summary.Albums);
        Assert.Equal(2, album.Episodes.Count);
        // 同日・単一フォルダ(part=0)でも放送時刻で区別できること(ADR-0002。受け手が重複表示しない)。
        Assert.Equal(new TimeOnly(8, 30, 0), album.Episodes[0].BroadcastTime);
        Assert.Equal(new TimeOnly(10, 0, 0), album.Episodes[1].BroadcastTime);
    }

    [Fact]
    public async Task DryRun_DoesNotPublishSummary()
    {
        await Build([Rec(StandardName)]).RunOnceAsync(dryRun: true);

        Assert.Equal(0, _publisher.Calls);
    }

    [Fact]
    public async Task Run_NoImports_DoesNotPublishSummary()
    {
        var junk = Rec("この時間はＮＨＫネットラジオでは_20241021200956.m4a");
        var report = await Build([junk], skipPatterns: ["この時間はＮＨＫネットラジオでは"]).RunOnceAsync(dryRun: false);

        Assert.Equal(0, _publisher.Calls);
        Assert.False(report.NotificationPublished);
    }

    [Fact]
    public async Task Run_PublishFailure_DoesNotFailImport_ButReportsError()
    {
        _publisher.Throws = new InvalidOperationException("event log full");

        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false);

        // 取り込みは成功(台帳記録済み)。発行失敗は報告するが巻き戻さない。
        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Single(_ledger.Keys);
        Assert.False(report.NotificationPublished);
        Assert.Equal("event log full", report.NotificationError);
    }
}
