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
/// 差し替え放送(ADR-0021)の取り込み。ファイル名の番組名がフォルダ名を含まず判断も無い回は保留し、判断があればそれに従う。
/// 保留のストアと保留シグナル(初めて保留したときだけ)も確かめる。
/// </summary>
public sealed class SubstituteBroadcastIngestTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private const string Station = "ニッポン放送";
    private const string Folder = "パンサー向井のチャリで30分";
    private const string Substitute = "辛坊治郎 ズーム そこまで言うか！";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ListSource(IReadOnlyList<RecordingFile> files) : IRecordingSource
    {
        public IEnumerable<RecordingFile> Enumerate() => files;
    }

    private sealed class MemoryLedger : ILedger
    {
        public List<LedgerEntry> Entries { get; } = [];
        public bool Contains(IdentityKey key, TimeOnly? broadcastTime, int partIndex) =>
            Entries.Any(e => e.IdentityKey == key.Value && e.BroadcastTime == broadcastTime && e.PartIndex == partIndex);
        public bool ContainsSource(string sourcePath) => Entries.Any(e => e.SourcePath == sourcePath);
        public bool ContainsContent(FileFingerprint fingerprint, DateOnly broadcastDate, TimeOnly? broadcastTime) => false;
        public void Record(LedgerEntry entry) => Entries.Add(entry);
        public bool Replace(LedgerEntry current, LedgerEntry updated) => throw new NotSupportedException();
        public bool Remove(LedgerEntry entry) => throw new NotSupportedException();
        public IReadOnlyList<LedgerEntry> ListAll() => Entries;
    }

    private sealed class MemoryTarget : IOutputTarget
    {
        public List<string> Written { get; } = [];
        public OutputPlan Plan(RecordingFile s, Episode e, NormalizedTags t) =>
            new() { Path = $"/out/{e.Station}/{e.Program}/{e.BroadcastDate:yyyy-MM-dd}.m4a" };
        public Task<string> WriteAsync(RecordingFile s, OutputPlan plan, NormalizedTags t, CancellationToken ct = default)
        {
            Written.Add(plan.Path);
            return Task.FromResult(plan.Path);
        }
        public int CleanupOrphans() => 0;
    }

    private sealed class NoTags : ITagReader
    {
        public string? ReadArtist(string filePath) => null;
        public RawTags ReadTags(string filePath) => new(null, null);
    }

    private sealed class MemoryHeldStore : IHeldRecordingStore
    {
        public Dictionary<string, HeldRecording> Rows { get; } = [];
        public bool IsHeld(string sourcePath) => Rows.ContainsKey(sourcePath);
        public void Record(HeldRecording held) => Rows[held.SourcePath] = held;
        public void Clear(string sourcePath) => Rows.Remove(sourcePath);
        public IReadOnlyList<HeldRecording> List() => Rows.Values.ToList();
    }

    private sealed class HeldPublisher : IIngestHeldPublisher
    {
        public List<IngestHeldSignal> Signals { get; } = [];
        public Exception? Throws { get; set; }
        public Task PublishAsync(IngestHeldSignal signal, CancellationToken cancellationToken = default)
        {
            Signals.Add(signal);
            return Throws is null ? Task.CompletedTask : throw Throws;
        }
    }

    private readonly MemoryLedger _ledger = new();
    private readonly MemoryTarget _target = new();
    private readonly MemoryHeldStore _held = new();
    private readonly HeldPublisher _heldPublisher = new();

    private static RecordingFile Rec(string filenameProgram, string stamp, string folder = Folder, int ageSeconds = 60) => new()
    {
        FilePath = $"/in/{Station}/{folder}/[{Station}]{filenameProgram}(TimeFree)_{stamp}.m4a",
        FileName = $"[{Station}]{filenameProgram}(TimeFree)_{stamp}.m4a",
        StationFolder = Station,
        ProgramFolder = folder,
        Extension = ".m4a",
        SizeBytes = 1000 + stamp.GetHashCode() % 100,
        LastWriteTimeUtc = Now.AddSeconds(-ageSeconds),
    };

    private static ProgramMasterConfig Decide(
        FilenameProgramTreatment treat, string? album = null, string name = Substitute,
        FilenameProgramMatchMode mode = FilenameProgramMatchMode.Exact) => new()
    {
        FilenamePrograms =
        [
            new FilenameProgramDecision
            {
                Match = new FilenameProgramMatch { Station = Station, Folder = Folder, FilenameProgram = name, Mode = mode },
                Treat = treat,
                Album = album,
            },
        ],
    };

    private IngestService Build(IReadOnlyList<RecordingFile> files, ProgramMasterConfig? programs = null) => new(
        new ListSource(files),
        new RajirekoImporter(),
        new StabilityChecker(new FixedClock(Now), TimeSpan.FromSeconds(15)),
        new RecordingSkipPolicy([]),
        new NoTags(),
        new EpisodeAssembler(new StationResolver(new StationMasterConfig()), new ProgramResolver(programs ?? new ProgramMasterConfig())),
        _target,
        _ledger,
        new FixedClock(Now),
        "ラジオ",
        new NullLibraryRefresher(),
        new NullIngestSummaryPublisher(),
        new NullIngestFailureStore(),
        new NullIngestFailurePublisher(),
        _held,
        _heldPublisher);

    [Fact]
    public async Task フォルダ名を含む回は取り込み_含まない回は保留する()
    {
        var regular = Rec($"{Folder} ゲスト回", "20250106000000");
        var substitute = Rec(Substitute, "20250113000000");

        var report = await Build([regular, substitute]).RunOnceAsync(dryRun: false);

        Assert.Equal(IngestOutcome.Imported, report.Items[0].Outcome);
        Assert.Equal(IngestOutcome.Held, report.Items[1].Outcome);
        Assert.Equal(Substitute, report.Items[1].FilenameProgram);
        Assert.Single(_target.Written);
        Assert.DoesNotContain(_ledger.Entries, e => e.SourcePath == substitute.FilePath);
    }

    [Fact]
    public async Task 統合番組のアルバム名を含む回は_フォルダ名を含まなくても取り込む()
    {
        // #241:統合番組のフォルダ名には分割の印が付き、ファイル名の番組名(時間帯の書き方が回ごとに違う)は含まない。
        const string album = "パンサー向井のチャリ";
        var programs = new ProgramMasterConfig
        {
            Programs = [new ProgramAlias
            {
                Match = new ProgramMatch { Station = Station, Folders = [album + "1", album + "2"] },
                Canonical = new ProgramCanonical { Album = album },
            }],
        };
        var first = Rec(album + " 10時～10時55分", "20250106100000", folder: album + "1");
        var second = Rec(album + " 11時～12時", "20250106110000", folder: album + "2");
        var substitute = Rec(Substitute, "20250113100000", folder: album + "1");

        var report = await Build([first, second, substitute], programs).RunOnceAsync(dryRun: false);

        Assert.Equal(
            [IngestOutcome.Imported, IngestOutcome.Imported, IngestOutcome.Held],
            report.Items.Select(i => i.Outcome));
        Assert.Equal(2, _target.Written.Count);
        Assert.Equal([substitute.FilePath], _held.Rows.Keys);
    }

    [Fact]
    public async Task 表記ゆれは照合キーで吸収する()
    {
        // 全角英数・空白の違い(ANN0 のゲスト名入りの回と同じく、フォルダ名を含めば取り込む)。
        var file = Rec("ＩＭＰのオールナイトニッポン０（ＺＥＲＯ）", "20251221000000", folder: "オールナイトニッポン0(ZERO)");

        var report = await Build([file]).RunOnceAsync(dryRun: false);

        Assert.Equal(IngestOutcome.Imported, report.Items.Single().Outcome);
    }

    [Fact]
    public async Task 初めて保留したときだけ保留シグナルを出し_ストアに記録する()
    {
        var first = Rec(Substitute, "20250113000000");

        var report1 = await Build([first]).RunOnceAsync(dryRun: false);
        var report2 = await Build([first]).RunOnceAsync(dryRun: false);

        Assert.True(report1.HeldNotificationPublished);
        Assert.False(report2.HeldNotificationPublished);
        var signal = Assert.Single(_heldPublisher.Signals);
        Assert.Equal(1, signal.HeldCount);
        var group = Assert.Single(signal.Groups);
        Assert.Equal((Station, Folder, Substitute, 1), (group.Station, group.Folder, group.FilenameProgram, group.Count));

        var row = Assert.Single(_held.Rows.Values);
        Assert.Equal(new HeldRecording(first.FilePath, Station, Folder, Substitute, Now), row);
    }

    [Fact]
    public async Task 新しく保留した回があれば_前からの保留とは別にもう一度出す()
    {
        var first = Rec(Substitute, "20250113000000");
        await Build([first]).RunOnceAsync(dryRun: false);

        var second = Rec(Substitute, "20250120000000");
        await Build([first, second]).RunOnceAsync(dryRun: false);

        Assert.Equal(2, _heldPublisher.Signals.Count);
        Assert.Equal(1, _heldPublisher.Signals[1].HeldCount); // 新しい 1 件だけ
        Assert.Equal(2, _held.Rows.Count);
    }

    [Fact]
    public async Task 発行に失敗しても保留は記録する()
    {
        _heldPublisher.Throws = new InvalidOperationException("トースト失敗");

        var report = await Build([Rec(Substitute, "20250113000000")]).RunOnceAsync(dryRun: false);

        Assert.False(report.HeldNotificationPublished);
        Assert.Equal("トースト失敗", report.HeldNotificationError);
        Assert.Single(_held.Rows); // 要対応には出る
    }

    [Fact]
    public async Task dry_run_は保留を報告するが_ストアもシグナルも触らない()
    {
        var report = await Build([Rec(Substitute, "20250113000000")]).RunOnceAsync(dryRun: true);

        Assert.Equal(IngestOutcome.Held, report.Items.Single().Outcome);
        Assert.Empty(_held.Rows);
        Assert.Empty(_heldPublisher.Signals);
    }

    [Fact]
    public async Task この番組の回と判断すればフォルダの番組として取り込み_保留を消す()
    {
        var file = Rec(Substitute, "20250113000000");
        await Build([file]).RunOnceAsync(dryRun: false);

        var report = await Build([file], Decide(FilenameProgramTreatment.SameProgram)).RunOnceAsync(dryRun: false);

        Assert.Equal(IngestOutcome.Imported, report.Items.Single().Outcome);
        Assert.Equal(Folder, _ledger.Entries.Single().Program);
        Assert.Empty(_held.Rows);
    }

    [Fact]
    public async Task 取り込まないと判断すれば取り込まず_保留を消す()
    {
        var file = Rec(Substitute, "20250113000000");
        await Build([file]).RunOnceAsync(dryRun: false);

        var report = await Build([file], Decide(FilenameProgramTreatment.Skip)).RunOnceAsync(dryRun: false);

        Assert.Equal(IngestOutcome.SkippedByDecision, report.Items.Single().Outcome);
        Assert.Empty(_target.Written);
        Assert.Empty(_ledger.Entries);
        Assert.Empty(_held.Rows);
    }

    [Fact]
    public async Task 別の番組として取り込むと判断すれば_判断のアルバムに取り込む()
    {
        var file = Rec(Substitute, "20250113000000");

        var report = await Build([file], Decide(FilenameProgramTreatment.OtherProgram, "辛坊治郎 ズーム")).RunOnceAsync(dryRun: false);

        var item = report.Items.Single();
        Assert.Equal(IngestOutcome.Imported, item.Outcome);
        Assert.Equal("辛坊治郎 ズーム", item.Episode!.Program);
        Assert.Null(item.Episode.PartIndex);
        Assert.Equal(Station, item.Episode.Station);
        Assert.Equal("辛坊治郎 ズーム", _ledger.Entries.Single().Program);
    }

    [Fact]
    public async Task 統合番組の2つのフォルダから同じ日に別の番組へ入る2回は_パートで分けて両方取り込む()
    {
        // #239:2 つの枠にまたがって放送された特番(ふらっと (1)・(2) のニューイヤー駅伝など)を 1 つのアルバムにまとめる。
        const string folder2 = Folder + "Part2";
        var programs = new ProgramMasterConfig
        {
            Programs =
            [
                new ProgramAlias
                {
                    Match = new ProgramMatch { Station = Station, Folders = [Folder, folder2] },
                    Canonical = new ProgramCanonical { Album = "チャリ" },
                },
            ],
            FilenamePrograms = [.. new[] { Folder, folder2 }.Select(folder => new FilenameProgramDecision
            {
                Match = new FilenameProgramMatch { Station = Station, Folder = folder, FilenameProgram = Substitute },
                Treat = FilenameProgramTreatment.OtherProgram,
                Album = "辛坊治郎 ズーム",
            })],
        };
        var first = Rec(Substitute, "20250113000000");
        var second = Rec(Substitute, "20250113010000", folder: folder2);

        var report = await Build([first, second], programs).RunOnceAsync(dryRun: false);

        Assert.All(report.Items, i => Assert.Equal(IngestOutcome.Imported, i.Outcome));
        Assert.Equal([1, 2], report.Items.Select(i => i.Episode!.PartIndex!.Value).Order());
        Assert.All(_ledger.Entries, e => Assert.Equal("辛坊治郎 ズーム", e.Program));
        Assert.Equal([1, 2], _ledger.Entries.Select(e => e.PartIndex).Order());
    }

    [Fact]
    public async Task 判断は別の組には効かない()
    {
        // 同じフォルダでも、ファイル名の番組名が違えば別の組(判断は組ごと)。
        var other = Rec("timeleszのオールナイトニッポンPremium", "20250203000000");

        var report = await Build([other], Decide(FilenameProgramTreatment.SameProgram)).RunOnceAsync(dryRun: false);

        Assert.Equal(IngestOutcome.Held, report.Items.Single().Outcome);
    }

    [Fact]
    public async Task 前方一致の判断は_ゲスト名の違う回をまとめて扱う()
    {
        // #240:野村萬斎のラジオで福袋【ゲスト】○○ のように、ゲストが変わるたびに組が分かれても 1 つの判断で取り込む。
        var a = Rec("野村萬斎のラジオで福袋【ゲスト】中越典子", "20250113000000");
        var b = Rec("野村萬斎のラジオで福袋【ゲスト】羽生結弦　パート１", "20250120000000");
        var programs = Decide(FilenameProgramTreatment.OtherProgram, "野村萬斎のラジオで福袋", "野村萬斎のラジオで福袋",
            FilenameProgramMatchMode.Prefix);

        var report = await Build([a, b], programs).RunOnceAsync(dryRun: false);

        Assert.All(report.Items, i => Assert.Equal(IngestOutcome.Imported, i.Outcome));
        Assert.All(_ledger.Entries, e => Assert.Equal("野村萬斎のラジオで福袋", e.Program));
    }

    [Fact]
    public async Task 部分一致の判断は_前に付くスポンサー名の違う回をまとめて扱う()
    {
        // #240:イースト駅前クリニック presents / ブレインスリープ presents のように、前に付くスポンサーが替わっても 1 つの判断で取り込む。
        var a = Rec("イースト駅前クリニック presents 川島明のねごと", "20250113000000");
        var b = Rec("ブレインスリープ presents 川島明のねごと", "20250120000000");
        var programs = Decide(FilenameProgramTreatment.SameProgram, name: "川島明のねごと", mode: FilenameProgramMatchMode.Contains);

        var report = await Build([a, b], programs).RunOnceAsync(dryRun: false);

        Assert.All(report.Items, i => Assert.Equal(IngestOutcome.Imported, i.Outcome));
        Assert.All(_ledger.Entries, e => Assert.Equal(Folder, e.Program));
    }

    [Fact]
    public async Task 取込元から消えた保留は消え_列挙が0件の走査では消さない()
    {
        var file = Rec(Substitute, "20250113000000");
        await Build([file]).RunOnceAsync(dryRun: false);

        await Build([]).RunOnceAsync(dryRun: false);
        Assert.Single(_held.Rows); // 取込元が一時的に見えないだけかもしれない

        await Build([Rec($"{Folder} ゲスト回", "20250106000000")]).RunOnceAsync(dryRun: false);
        Assert.Empty(_held.Rows);
    }

    [Fact]
    public async Task まだ安定していない走査では保留を消さない()
    {
        var file = Rec(Substitute, "20250113000000");
        await Build([file]).RunOnceAsync(dryRun: false);

        // 書き直されて mtime が新しくなった(安定待ち)。判断は変わっていないので保留のまま。
        await Build([file with { LastWriteTimeUtc = Now }]).RunOnceAsync(dryRun: false);

        Assert.Single(_held.Rows);
        Assert.Single(_heldPublisher.Signals); // 次に安定しても、もう一度は出さない
    }

    [Fact]
    public async Task 取り込み済みの回は保留しない()
    {
        // 判断待ちの取り込み済みの回は「済」(要対応が台帳から見つける)。導入した直後に過去の回がまとめて保留にならない。
        var file = Rec(Substitute, "20250113000000");
        await Build([file], Decide(FilenameProgramTreatment.SameProgram)).RunOnceAsync(dryRun: false);

        var report = await Build([file]).RunOnceAsync(dryRun: false);

        Assert.Equal(IngestOutcome.SkippedAlreadyImported, report.Items.Single().Outcome);
        Assert.Empty(_heldPublisher.Signals);
    }
}
