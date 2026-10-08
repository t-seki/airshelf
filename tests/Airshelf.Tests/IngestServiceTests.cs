using Airshelf.Core.Configuration;
using Airshelf.Core.Ingestion;
using Airshelf.Core.Jellyfin;
using Airshelf.Core.Ledger;
using Airshelf.Core.Model;
using Airshelf.Core.Normalization;
using Airshelf.Core.Output;
using Airshelf.Core.Sources;
using Airshelf.Hosting.Control;

namespace Airshelf.Tests;

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

    // 列挙の途中で投げる取込元(NAS に届かなくなった等)。先に渡したファイルを返してから投げる。
    private sealed class ThrowingRecordingSource(params RecordingFile[] files) : IRecordingSource
    {
        public IEnumerable<RecordingFile> Enumerate()
        {
            foreach (var file in files)
                yield return file;
            throw new IOException("取込元に届きません(テスト)");
        }
    }

    // 先に渡したファイルを返したところで取り消し(常駐の停止要求)を起こし、残りを返す取込元。
    private sealed class CancelingRecordingSource(
        CancellationTokenSource cancel, RecordingFile[] before, params RecordingFile[] after) : IRecordingSource
    {
        public IEnumerable<RecordingFile> Enumerate()
        {
            foreach (var file in before)
                yield return file;
            cancel.Cancel();
            foreach (var file in after)
                yield return file;
        }
    }

    private sealed class FakeLedger : ILedger
    {
        private readonly List<(string Key, TimeOnly? Time, int Part)> _entries = [];
        private readonly List<(FileFingerprint Fp, DateOnly Date, TimeOnly? Time)> _content = [];
        public HashSet<string> Sources { get; } = [];
        public IReadOnlyList<string> Keys => _entries.Select(e => e.Key).ToList();

        // 厳密一致(ADR-0004)。時刻 or part が異なれば別放送。
        public bool Contains(IdentityKey key, TimeOnly? broadcastTime, int partIndex) =>
            _entries.Any(e => e.Key == key.Value && e.Time == broadcastTime && e.Part == partIndex);

        public bool ContainsSource(string sourcePath) => Sources.Contains(sourcePath);

        // content ゲート(#79 / ADR-0014)。パス/フォルダ非依存で内容 + 放送日時が一致すれば取込済み。
        public bool ContainsContent(FileFingerprint fingerprint, DateOnly broadcastDate, TimeOnly? broadcastTime) =>
            _content.Any(c => c.Fp == fingerprint && c.Date == broadcastDate && c.Time == broadcastTime);

        public void Record(LedgerEntry entry)
        {
            _entries.Add((entry.IdentityKey, entry.BroadcastTime, entry.PartIndex));
            Sources.Add(entry.SourcePath);
            _content.Add((entry.Fingerprint, entry.BroadcastDate, entry.BroadcastTime));
        }

        // 取込は書き換えない(再適用だけが使う。ADR-0016)。
        public bool Replace(LedgerEntry current, LedgerEntry updated) => throw new NotSupportedException();

        public bool Remove(LedgerEntry entry) => throw new NotSupportedException();

        public IReadOnlyList<LedgerEntry> ListAll() => []; // 取込テストでは履歴列挙を使わない
    }

    private sealed class FakeOutputTarget : IOutputTarget
    {
        public List<string> Written { get; } = [];
        public OutputPlan Plan(RecordingFile s, Episode e, NormalizedTags t) => new() { Path = $"/out/{e.Program}.m4a" };
        public Task<string> WriteAsync(RecordingFile s, OutputPlan plan, NormalizedTags t, CancellationToken ct = default)
        {
            Written.Add(plan.Path);
            return Task.FromResult(plan.Path);
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
        public OutputPlan Plan(RecordingFile s, Episode e, NormalizedTags t) => new() { Path = "/out/plan.m4a" };
        public Task<string> WriteAsync(RecordingFile s, OutputPlan plan, NormalizedTags t, CancellationToken ct = default)
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

        public Task<string?> PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = summary;
            return Throws is null ? Task.FromResult<string?>(null) : throw Throws;
        }
    }

    private sealed class FakeFailurePublisher : IIngestFailurePublisher
    {
        public int Calls { get; private set; }
        public IngestFailureSignal? Last { get; private set; }
        public Exception? Throws { get; set; }

        public Task PublishAsync(IngestFailureSignal signal, CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = signal;
            return Throws is null ? Task.CompletedTask : throw Throws;
        }
    }

    private sealed class FakeFailureStore : IIngestFailureStore
    {
        private readonly Dictionary<string, (FileFingerprint Fingerprint, string Reason, DateTimeOffset At)> _known = [];
        public List<string> Cleared { get; } = [];
        public Exception? ThrowOnIsKnown { get; set; }
        public Exception? ThrowOnRecord { get; set; }

        public bool IsKnown(string sourcePath, FileFingerprint fingerprint)
        {
            if (ThrowOnIsKnown is not null) throw ThrowOnIsKnown;
            return _known.TryGetValue(sourcePath, out var known) && known.Fingerprint == fingerprint;
        }

        public void Record(string sourcePath, FileFingerprint fingerprint, string reason, DateTimeOffset failedAtUtc)
        {
            if (ThrowOnRecord is not null) throw ThrowOnRecord;
            _known[sourcePath] = (fingerprint, reason, failedAtUtc);
        }

        // 前もって記録しておく(以前の走査で失敗して記録済み、を作る)。
        public void Seed(string sourcePath, FileFingerprint fingerprint)
            => _known[sourcePath] = (fingerprint, "以前の失敗", Now);

        public void Clear(string sourcePath)
        {
            Cleared.Add(sourcePath);
            _known.Remove(sourcePath);
        }

        public IReadOnlyList<IngestFailureRecord> List()
            => _known.Select(k => new IngestFailureRecord(k.Key, k.Value.Reason, k.Value.At)).ToList();

        public IngestFailureRecord? Get(string sourcePath)
            => _known.TryGetValue(sourcePath, out var k) ? new IngestFailureRecord(sourcePath, k.Reason, k.At) : null;

        public int Count => _known.Count;
    }

    private sealed class PathLeakingOutputTarget : IOutputTarget
    {
        public OutputPlan Plan(RecordingFile s, Episode e, NormalizedTags t) => new() { Path = "/out/plan.m4a" };
        // 例外メッセージにフルパスを埋める(実 IO 例外を模す)。sanitize がこれを畳むかを検証する。
        public Task<string> WriteAsync(RecordingFile s, OutputPlan plan, NormalizedTags t, CancellationToken ct = default)
            => throw new IOException($"アクセスが拒否されました: {s.FilePath}");
        public int CleanupOrphans() => 0;
    }

    private readonly FakeLedger _ledger = new();
    private readonly FakeOutputTarget _target = new();
    private readonly FakeRefresher _refresher = new();
    private readonly FakePublisher _publisher = new();
    private readonly FakeFailurePublisher _failurePublisher = new();
    private readonly FakeFailureStore _failureStore = new();
    private readonly FakeTagReader _tagReader = new("出演者");

    private IngestService Build(
        IEnumerable<RecordingFile> files, IEnumerable<string>? skipPatterns = null,
        IOutputTarget? target = null, ILibraryRefresher? refresher = null, IRecordingSource? source = null,
        IIngestSummaryPublisher? publisher = null)
        => new(
            source ?? new FakeRecordingSource(files.ToArray()),
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
            refresher ?? _refresher,
            publisher ?? _publisher,
            _failureStore,
            _failurePublisher);

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
    private static string OtherWeek(int day) => $"[J-WAVE]LOGISTEED RADIONOMICS(TimeFree)_202606{day:D2}223000.m4a";

    [Fact]
    public async Task DryRun_NewFile_IsPlanned_NoWriteNoLedger()
    {
        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.DryRun);
        Assert.Equal(1, report.Count(IngestOutcome.Planned));
        Assert.Empty(_target.Written);
        Assert.Empty(_ledger.Keys);
    }

    [Fact]
    public async Task Run_NewFile_IsImported_AndRecorded()
    {
        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

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

        var report = await Build(files).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Count(IngestOutcome.Imported));
        Assert.Equal(2, _target.Written.Count);
    }

    [Fact]
    public async Task Run_Twice_SecondRunIsIdempotentSkip()
    {
        var files = new[] { Rec(StandardName) };
        await Build(files).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        var second = await Build(files).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, second.Count(IngestOutcome.SkippedAlreadyImported));
        Assert.Single(_target.Written); // 2 回目は書き込まない
    }

    [Fact]
    public async Task SameContent_RenamedProgramFolder_IsSkippedByContentGate()
    {
        // #79 の事故再現: 同一内容(同 size + 同 mtime・同ファイル名タイムスタンプ)を、番組フォルダ名だけ
        // 変えて再取込。identity(フォルダ由来 ADR-0001)も source パスも変わり Contains/ContainsSource は
        // 外れるが、content ゲート(フィンガープリント + 放送日時)が捕捉して重複コピーを作らない。
        var first = Rec("[TBSラジオ]パンサー向井のふらっと(1)(TimeFree)_20241223083000.m4a",
            station: "TBSラジオ", program: "パンサー向井のふらっと(1)", size: 5000);
        await Build([first]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(_target.Written);

        var renamed = Rec("[TBSラジオ]パンサー向井のふらっと(2)(TimeFree)_20241223083000.m4a",
            station: "TBSラジオ", program: "パンサー向井のふらっと(2)", size: 5000);
        var second = await Build([renamed]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, second.Count(IngestOutcome.SkippedAlreadyImported));
        Assert.Single(_target.Written); // 2 回目は書き込まない(… 0830 重複を生成しない)
    }

    [Fact]
    public async Task DifferentContent_SameSlot_BothImported_ContentGateNotOverAggressive()
    {
        // content ゲートが過剰にスキップしない保証: 同じ放送日時でも内容(size)が違えば別物として両方取り込む。
        // フィンガープリントが放送日時だけで誤一致 → 別放送を取りこぼす、を防ぐ(advisor 指摘の非対称リスク)。
        var a = Rec("[TBSラジオ]番組A(TimeFree)_20241223083000.m4a",
            station: "TBSラジオ", program: "番組A", size: 5000);
        var b = Rec("[TBSラジオ]番組B(TimeFree)_20241223083000.m4a",
            station: "TBSラジオ", program: "番組B", size: 6000);

        var report = await Build([a, b]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Count(IngestOutcome.Imported));
        Assert.Equal(2, _target.Written.Count);
    }

    [Fact]
    public async Task Junk_MatchingSkipPattern_IsSkipped()
    {
        var junk = Rec("この時間はＮＨＫネットラジオではお聞き頂けません。_20241021200956.m4a",
            station: "NHKラジオ第1(radiko)", program: "番組");

        var report = await Build([junk], skipPatterns: ["この時間はＮＨＫネットラジオでは"]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Count(IngestOutcome.SkippedJunk));
        Assert.Empty(_target.Written);
    }

    [Fact]
    public async Task ParseFailure_IsReportedAsFailed()
    {
        var bad = Rec("[X局]番組(TimeFree)_20250229123000.m4a"); // 非閏年の2/29

        var report = await Build([bad]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Count(IngestOutcome.Failed));
    }

    // ---- 取込失敗シグナル(ADR-0011)----

    private static RecordingFile BadFile() => Rec("[X局]番組(TimeFree)_20250229123000.m4a"); // 非閏年 2/29 → Failed

    [Fact]
    public async Task NewFailure_発行され記録される()
    {
        var bad = BadFile();

        var report = await Build([bad]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.FailureNotificationPublished);
        Assert.Equal(1, _failurePublisher.Calls);
        Assert.Equal(1, _failurePublisher.Last!.FailedCount);
        // 発行成功後に de-dup 記録されている。
        Assert.True(_failureStore.IsKnown(bad.FilePath, FileFingerprint.Of(bad)));
    }

    [Fact]
    public async Task SameFailure_二度目は再発行されない_de_dup()
    {
        var bad = BadFile();
        await Build([bad]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        var second = await Build([bad]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, _failurePublisher.Calls);        // 2 回目は de-dup で抑制
        Assert.False(second.FailureNotificationPublished);
    }

    [Fact]
    public async Task FailurePublishが投げたら記録せず次走査で再試行する()
    {
        var bad = BadFile();
        _failurePublisher.Throws = new InvalidOperationException("発行失敗");

        var first = await Build([bad]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(first.FailureNotificationPublished);
        Assert.NotNull(first.FailureNotificationError);
        Assert.False(_failureStore.IsKnown(bad.FilePath, FileFingerprint.Of(bad))); // 発行前に記録しない

        // 復旧後の走査で再試行される(発行前記録なら「通知済み」と誤認して永久に見逃す)。
        _failurePublisher.Throws = null;
        var second = await Build([bad]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(second.FailureNotificationPublished);
        Assert.Equal(2, _failurePublisher.Calls);
    }

    [Fact]
    public async Task 取込成功はそのパスの失敗記録を解除する()
    {
        var good = Rec(StandardName);
        _failureStore.Seed(good.FilePath, FileFingerprint.Of(good)); // 以前失敗して記録済みと仮定

        await Build([good]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(good.FilePath, _failureStore.Cleared);
        Assert.False(_failureStore.IsKnown(good.FilePath, FileFingerprint.Of(good)));
    }

    // ---- 要対応の「解決していない取込失敗」(#172)----

    [Fact]
    public async Task 失敗は整形した理由と記録した時刻を持つ()
    {
        var file = Rec(StandardName);

        await Build([file], target: new PathLeakingOutputTarget()).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        var record = _failureStore.Get(file.FilePath);
        Assert.NotNull(record);
        Assert.Equal(_failurePublisher.Last!.Files.Single().Reason, record.Reason); // トーストと同じ文言
        Assert.DoesNotContain(file.FilePath, record.Reason);
        Assert.Equal(Now, record.FailedAtUtc);
    }

    [Fact]
    public async Task 取込元から消えたファイルの失敗記録は走査で消える()
    {
        var gone = Rec("[X局]番組(TimeFree)_20250101120000.m4a", program: "消えた番組");
        _failureStore.Seed(gone.FilePath, FileFingerprint.Of(gone)); // 以前失敗したが、今は取込元に無い
        var bad = BadFile();

        await Build([Rec(StandardName), bad]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(_failureStore.Get(gone.FilePath));
        Assert.NotNull(_failureStore.Get(bad.FilePath)); // まだ取込元にある失敗は残る
    }

    [Fact]
    public async Task 列挙が0件の走査では失敗記録を消さない()
    {
        // 取込元が一時的に見えない(NAS に届かない・フォルダが無い)と、列挙は 0 件で終わる。
        var bad = BadFile();
        _failureStore.Seed(bad.FilePath, FileFingerprint.Of(bad));

        await Build([]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(_failureStore.Get(bad.FilePath));
        Assert.Empty(_failureStore.Cleared);
    }

    [Fact]
    public async Task 列挙が例外で終わった走査では失敗記録を消さない()
    {
        var gone = Rec("[X局]番組(TimeFree)_20250101120000.m4a", program: "消えた番組");
        _failureStore.Seed(gone.FilePath, FileFingerprint.Of(gone));
        var service = Build([], source: new ThrowingRecordingSource(Rec(StandardName)));

        await Assert.ThrowsAsync<IOException>(() => service.RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken));

        Assert.NotNull(_failureStore.Get(gone.FilePath));
    }

    [Fact]
    public async Task 取り消しで打ち切った走査は_それまでの明細を持つ例外で終わる()
    {
        // 常駐の停止要求で走査を打ち切ったとき、呼び手(RadioMonitorWorker)がそれまでの件数を gui.log に残せるように(#218)。
        using var cancel = new CancellationTokenSource();
        var source = new CancelingRecordingSource(cancel, [Rec(StandardName), BadFile()], Rec(OtherWeek(19)));
        var service = Build([], source: source);

        var ex = await Assert.ThrowsAsync<IngestCanceledException>(() => service.RunOnceAsync(dryRun: false, cancel.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex); // 今までの「停止要求はループへ伝える」がそのまま効く
        Assert.Equal(cancel.Token, ex.CancellationToken);
        Assert.Equal(1, ex.Count(IngestOutcome.Imported));
        Assert.Equal(1, ex.Count(IngestOutcome.Failed));
        Assert.Single(_target.Written); // 打ち切った後のファイルは処理しない
        Assert.Equal(0, _publisher.Calls); // 打ち切った走査は要約を出さない(今までどおり)
    }

    [Fact]
    public async Task DryRunでは失敗記録を消さない()
    {
        var gone = Rec("[X局]番組(TimeFree)_20250101120000.m4a", program: "消えた番組");
        _failureStore.Seed(gone.FilePath, FileFingerprint.Of(gone));

        await Build([Rec(StandardName)]).RunOnceAsync(dryRun: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(_failureStore.Get(gone.FilePath));
    }

    [Fact]
    public async Task パスの表記が変わっても_同じファイルの失敗記録は1行だけ残る()
    {
        // 実物の台帳で確かめる(ストアの主キーは大文字小文字を区別する)。前の走査では大文字の表記で失敗を記録し、
        // 今の走査では小文字の表記で同じファイルが列挙された。古い表記の行が残り続けないこと。
        var dir = Directory.CreateTempSubdirectory("airshelf-ingest-failures-").FullName;
        try
        {
            using var store = new SqliteLedger(Path.Combine(dir, "ledger.db"));
            var bad = BadFile();
            store.Record(bad.FilePath.ToUpperInvariant(), FileFingerprint.Of(bad), "前の走査の理由", Now);

            await BuildWithStore(store, [bad]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

            var record = Assert.Single(store.List());
            Assert.Equal(bad.FilePath, record.SourcePath);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    private IngestService BuildWithStore(IIngestFailureStore store, IEnumerable<RecordingFile> files)
        => new(
            new FakeRecordingSource(files.ToArray()),
            new RajirekoImporter(),
            new StabilityChecker(new FixedClock(Now), TimeSpan.FromSeconds(15)),
            new RecordingSkipPolicy([]),
            _tagReader,
            new EpisodeAssembler(
                new StationResolver(new StationMasterConfig()),
                new ProgramResolver(new ProgramMasterConfig())),
            _target,
            _ledger,
            new FixedClock(Now),
            genre: "ラジオ",
            _refresher,
            _publisher,
            store,
            _failurePublisher);

    [Fact]
    public async Task DryRun_失敗があっても発行しない()
    {
        var report = await Build([BadFile()]).RunOnceAsync(dryRun: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, _failurePublisher.Calls);
        Assert.False(report.FailureNotificationPublished);
    }

    [Fact]
    public async Task 失敗理由はフルパスを畳んでファイル名にする()
    {
        var file = Rec(StandardName);

        await Build([file], target: new PathLeakingOutputTarget()).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        var reason = _failurePublisher.Last!.Files.Single().Reason;
        Assert.DoesNotContain(file.FilePath, reason);      // フルパスは晒さない
        Assert.Contains(file.FileName, reason);            // ファイル名には畳まれる
    }

    [Fact]
    public async Task ストア読取が投げても取込は失敗にならず握って報告する()
    {
        // Clear/IsKnown が try 外だと成功走査が例外で「失敗」に化ける([0])。握って報告するだけにする。
        _failureStore.ThrowOnIsKnown = new InvalidOperationException("SQLite ロック(テスト)");
        var bad = BadFile();

        var report = await Build([bad]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken); // 例外が漏れないこと(漏れると throw)

        Assert.Equal(1, report.Count(IngestOutcome.Failed)); // 取込結果は正常に返る
        Assert.False(report.FailureNotificationPublished);
        Assert.NotNull(report.FailureNotificationError);
    }

    [Fact]
    public async Task 発行成功後のRecord失敗は発行成功を覆さない()
    {
        _failureStore.ThrowOnRecord = new InvalidOperationException("SQLite 書込(テスト)");
        var bad = BadFile();

        var report = await Build([bad]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        // publish は成功しているので published=true(記録失敗で false に化けさせない)。
        Assert.True(report.FailureNotificationPublished);
        Assert.Null(report.FailureNotificationError);
        Assert.Equal(1, _failurePublisher.Calls);
    }

    [Fact]
    public async Task RecentlyModified_IsSkippedAsUnstable()
    {
        var report = await Build([Rec(StandardName, ageSeconds: 5)]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Count(IngestOutcome.SkippedUnstable));
    }

    [Fact]
    public async Task WriteFailure_IsReportedAsFailed_AndDoesNotAbortRun()
    {
        var files = new[]
        {
            Rec("[J-WAVE]AAA(TimeFree)_20260612223000.m4a", program: "AAA"),
            Rec("[J-WAVE]BBB(TimeFree)_20260613223000.m4a", program: "BBB"),
        };

        var report = await Build(files, target: new ThrowingOutputTarget()).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        // 1 件目が失敗しても 2 件目まで走査が続き、両方 Failed として報告される。
        Assert.Equal(2, report.Items.Count);
        Assert.Equal(2, report.Count(IngestOutcome.Failed));
        Assert.Empty(_ledger.Keys); // 失敗は台帳に記録しない
    }

    [Fact]
    public async Task AlreadyImported_SkipsWithoutReadingArtistTag()
    {
        var files = new[] { Rec(StandardName) };
        await Build(files).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken); // 取込: 出演者を1回読む
        var afterImport = _tagReader.Reads;

        await Build(files).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken); // 2回目: 既取込 → タグを読まずにスキップ

        Assert.Equal(1, afterImport);
        Assert.Equal(afterImport, _tagReader.Reads); // 追加読取なし(ADR-0003 の高速化)
    }

    [Fact]
    public async Task Run_WithImport_TriggersLibraryRefresh()
    {
        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, _refresher.Calls);
        Assert.Equal(LibraryRefreshStatus.Started, report.LibraryRefresh);
        Assert.Null(report.RefreshError);
    }

    [Fact]
    public async Task Run_WithImport_JellyfinNotConfigured_ReportsNotConfigured()
    {
        // 未設定(NullLibraryRefresher)は呼べば成功で終わるが、「起動」と報告してはいけない(#133)。
        var report = await Build([Rec(StandardName)], refresher: new NullLibraryRefresher()).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Equal(LibraryRefreshStatus.NotConfigured, report.LibraryRefresh);
        Assert.Null(report.RefreshError);
    }

    [Fact]
    public async Task Run_WithImport_MisconfiguredJellyfin_ReportsMisconfiguredWithReason()
    {
        // JellyfinUrl の形式不正は未設定ではなく失敗として理由を報告する(#133)。
        // 繋がらない・エラー(Failed)とは分ける。再試行では直らないため(#191)。
        var report = await Build([Rec(StandardName)], refresher: new MisconfiguredLibraryRefresher("形式が不正"))
            .RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Equal(LibraryRefreshStatus.Misconfigured, report.LibraryRefresh);
        Assert.Equal("形式が不正", report.RefreshError);
    }

    [Fact]
    public async Task DryRun_DoesNotRefresh()
    {
        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, _refresher.Calls);
        Assert.Equal(LibraryRefreshStatus.NotNeeded, report.LibraryRefresh);
        Assert.Null(report.RefreshError);
    }

    [Fact]
    public async Task DryRun_JellyfinNotConfigured_ReportsNotNeeded()
    {
        // dry-run は未設定でも「不要」(今どおり何も出さない)。
        var report = await Build([Rec(StandardName)], refresher: new NullLibraryRefresher()).RunOnceAsync(dryRun: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LibraryRefreshStatus.NotNeeded, report.LibraryRefresh);
    }

    [Fact]
    public async Task Run_NoImports_DoesNotRefresh()
    {
        // ジャンクのみ = 取込0件 → リフレッシュは起動しない。
        var junk = Rec("この時間はＮＨＫネットラジオでは_20241021200956.m4a");
        var report = await Build([junk], skipPatterns: ["この時間はＮＨＫネットラジオでは"]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, _refresher.Calls);
        Assert.Equal(LibraryRefreshStatus.NotNeeded, report.LibraryRefresh);
        Assert.Null(report.RefreshError);
    }

    [Fact]
    public async Task Run_NoImports_JellyfinNotConfigured_ReportsNotNeeded()
    {
        // 取込0件なら未設定でも「不要」(今どおり何も出さない)。
        var junk = Rec("この時間はＮＨＫネットラジオでは_20241021200956.m4a");
        var report = await Build([junk], skipPatterns: ["この時間はＮＨＫネットラジオでは"], refresher: new NullLibraryRefresher())
            .RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LibraryRefreshStatus.NotNeeded, report.LibraryRefresh);
    }

    [Fact]
    public async Task Run_RefreshFailure_DoesNotFailImport_ButReportsError()
    {
        _refresher.Throws = new JellyfinException("boom");

        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        // 取り込み自体は成功(台帳記録済み)。更新失敗はエラーとして報告するが巻き戻さない。
        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Single(_ledger.Keys);
        Assert.Equal(LibraryRefreshStatus.Failed, report.LibraryRefresh);
        Assert.Equal("boom", report.RefreshError);
    }

    [Fact]
    public async Task Run_RefreshTimeout_IsCaughtAsError_NotPropagated()
    {
        // HttpClient のタイムアウトは TaskCanceledException(OperationCanceledException のサブクラス)。
        // 外部キャンセルではないので、伝播させず失敗として握ること。
        _refresher.Throws = new TaskCanceledException("timeout");

        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Equal(LibraryRefreshStatus.Failed, report.LibraryRefresh);
        Assert.Equal("timeout", report.RefreshError);
    }

    [Fact]
    public async Task Run_WithImport_PublishesSummary_WithProgramDateAndPart()
    {
        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

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
        Assert.Equal(1, album.EpisodeCount);
        Assert.Equal(new DateOnly(2026, 6, 12), album.FirstEpisode.BroadcastDate);
        Assert.Equal(new TimeOnly(22, 30, 0), album.FirstEpisode.BroadcastTime);
        Assert.Equal(0, album.FirstEpisode.PartIndex);
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

        await Build(files).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(_publisher.Last);
        var summary = _publisher.Last!;
        Assert.Equal(2, summary.TotalImported);
        var album = Assert.Single(summary.Albums);
        // 同日・単一フォルダ(part=0)の 2 放送が、1 アルバムの **2 件** として数えられること
        // (ADR-0002。通知が「1件」に潰れない)。同日別時刻を別放送回と見なす同一性判定そのものは
        // SqliteLedgerTests の「同日 tie-break(ADR-0002)」節で担保している。
        Assert.Equal(2, album.EpisodeCount);
        // 先頭 = **出現順の 1 件目**(この入力ではたまたま早い方)。時刻順である保証は無い —
        // 本番の列挙は Directory.EnumerateFiles で、.NET は順序を保証しない。
        Assert.Equal(new TimeOnly(8, 30, 0), album.FirstEpisode.BroadcastTime);
    }

    [Fact]
    public async Task DryRun_DoesNotPublishSummary()
    {
        await Build([Rec(StandardName)]).RunOnceAsync(dryRun: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, _publisher.Calls);
    }

    [Fact]
    public async Task Run_NoImports_DoesNotPublishSummary()
    {
        var junk = Rec("この時間はＮＨＫネットラジオでは_20241021200956.m4a");
        var report = await Build([junk], skipPatterns: ["この時間はＮＨＫネットラジオでは"]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, _publisher.Calls);
        Assert.False(report.NotificationPublished);
    }

    [Fact]
    public async Task Run_PublishFailure_DoesNotFailImport_ButReportsError()
    {
        _publisher.Throws = new InvalidOperationException("event log full");

        var report = await Build([Rec(StandardName)]).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        // 取り込みは成功(台帳記録済み)。発行失敗は報告するが巻き戻さない。
        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Single(_ledger.Keys);
        Assert.False(report.NotificationPublished);
        Assert.Equal("event log full", report.NotificationError);
    }

    // ---- 走査の実行(CLI から頼んだ走査。#180 / ADR-0020)----

    [Fact]
    public async Task 走査の実行は新規取込のトーストを出さず_失敗のトーストは出して失敗のストアに記録する()
    {
        var gate = new ScanRunSummaryGate(_publisher);
        var queue = new ScanRunQueue(gate);
        var bad = BadFile();
        var service = Build([Rec(StandardName), bad], publisher: gate);

        var pending = queue.Enqueue();
        await queue.RunAsync(queue.TakePending()!, ct => service.RunOnceAsync(dryRun: false, ct), CancellationToken.None);
        var result = await pending;

        Assert.Equal(0, _publisher.Calls);                   // 新規取込のトーストは出さない(結果は CLI に返る)
        Assert.Equal(1, _failurePublisher.Calls);            // 失敗のトーストは出す
        Assert.True(_failureStore.IsKnown(bad.FilePath, FileFingerprint.Of(bad))); // 要対応に載る
        Assert.Equal(1, result.Report!.Count(IngestOutcome.Imported));
        Assert.Equal(1, result.Report.Count(IngestOutcome.Failed));
        Assert.Null(result.Error);

    }

    [Fact]
    public async Task 走査の実行で止めた通知は_発行したと報告せず理由を返す()
    {
        var gate = new ScanRunSummaryGate(_publisher);
        var queue = new ScanRunQueue(gate);
        var service = Build([Rec(StandardName)], publisher: gate);
        IngestReport? report = null;

        var pending = queue.Enqueue();
        await queue.RunAsync(
            queue.TakePending()!, async ct => report = await service.RunOnceAsync(dryRun: false, ct), CancellationToken.None);
        await pending;

        // 出していない通知を「発行」と報告しない(gui.log の「通知」の欄。#215)。
        Assert.Equal(0, _publisher.Calls);
        Assert.False(report!.NotificationPublished);
        Assert.Equal(ScanRunSummaryGate.SuppressedReason, report.NotificationSuppressedReason);
        Assert.Null(report.NotificationError);
    }

    [Fact]
    public async Task 設定で新規取込のトーストを止めているときは_発行したと報告せず理由を返す()
    {
        var gate = new Airshelf.Gui.Core.Management.GatedIngestSummaryPublisher(_publisher, () => false);

        var report = await Build([Rec(StandardName)], publisher: gate).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, _publisher.Calls);
        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.False(report.NotificationPublished);
        Assert.Equal(
            Airshelf.Gui.Core.Management.GatedIngestSummaryPublisher.SuppressedReason,
            report.NotificationSuppressedReason);
    }

    [Fact]
    public async Task トレイと同じ入れ子では_走査の実行が設定より先に理由になる()
    {
        // トレイの組み方:設定の門(GatedIngestSummaryPublisher。Program.BuildHost)を、走査の実行の門
        // (ScanRunSummaryGate。RadioMonitorWorker のコンストラクタ)が外から包む。
        var enabled = false;
        var gate = new ScanRunSummaryGate(
            new Airshelf.Gui.Core.Management.GatedIngestSummaryPublisher(_publisher, () => enabled));
        var queue = new ScanRunQueue(gate);

        IngestReport? scanRun = null;
        var pending = queue.Enqueue();
        await queue.RunAsync(
            queue.TakePending()!,
            async ct => scanRun = await Build([Rec(StandardName)], publisher: gate).RunOnceAsync(dryRun: false, ct),
            CancellationToken.None);
        await pending;
        // 走査ごとに別の放送回を置く(同じ回は台帳で取込済スキップになり、要約が作られない)。
        var offOutside = await Build([Rec(OtherWeek(19), size: 2048)], publisher: gate).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        enabled = true;
        var shown = await Build([Rec(OtherWeek(26), size: 4096)], publisher: gate).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ScanRunSummaryGate.SuppressedReason, scanRun!.NotificationSuppressedReason);   // CLI の走査中・設定でオフ
        Assert.Equal(
            Airshelf.Gui.Core.Management.GatedIngestSummaryPublisher.SuppressedReason,
            offOutside.NotificationSuppressedReason);                                               // 走査の外・設定でオフ
        Assert.Null(shown.NotificationSuppressedReason);                                            // どちらでもない
        Assert.Equal(1, scanRun.Count(IngestOutcome.Imported));
        Assert.Equal(1, offOutside.Count(IngestOutcome.Imported));
        Assert.True(shown.NotificationPublished);
        Assert.Equal(1, _publisher.Calls);                                                          // 提示に届いたのは最後だけ
    }

    [Fact]
    public async Task 走査の実行の外で門を通って出したときは_発行と報告し理由は無い()
    {
        var gate = new ScanRunSummaryGate(_publisher);

        var report = await Build([Rec(StandardName)], publisher: gate).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, _publisher.Calls);
        Assert.True(report.NotificationPublished);
        Assert.Null(report.NotificationSuppressedReason);
    }

    [Fact]
    public async Task 走査の実行の外の走査は同じ門を通っても新規取込のトーストを出す()
    {
        var gate = new ScanRunSummaryGate(_publisher);
        var queue = new ScanRunQueue(gate);

        // 走査の実行を 1 回済ませた後でも、止めたままにならない。
        var scanRun = queue.Enqueue();
        await queue.RunAsync(
            queue.TakePending()!, ct => Build([], publisher: gate).RunOnceAsync(dryRun: false, ct), CancellationToken.None);
        await scanRun;
        await Build([Rec(StandardName)], publisher: gate).RunOnceAsync(dryRun: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, _publisher.Calls);
    }
}
