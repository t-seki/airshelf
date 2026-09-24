using System.IO.Abstractions;
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
/// 出力先の同名衝突(#79 / ADR-0015)を実ファイル・実台帳(SQLite)・実 TagLib で通す統合テスト。
/// 台帳を失った後の再走査で near-dup(<c>… 0830.m4a</c>)を作らないこと、同日・別時刻の別放送は
/// 従来どおり時刻付きで両方残ること(ADR-0002)を確かめる。
/// </summary>
public sealed class OutputCollisionIngestTests : IDisposable
{
    private const string Station = "TBSラジオ";
    private const string Program = "パンサー向井のふらっと";
    private const string At0830 = "[TBSラジオ]パンサー向井のふらっと(TimeFree)_20241223083000.m4a";
    private const string At1000 = "[TBSラジオ]パンサー向井のふらっと(TimeFree)_20241223100000.m4a";

    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "radirec-test-" + Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _ledgers = [];

    public OutputCollisionIngestTests() => Directory.CreateDirectory(_work);

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
    private string OutputDir => Path.Combine(OutputRoot, Station, Program);
    private string BaseOutput => Path.Combine(OutputDir, $"{Program} 2024-12-23.m4a");

    // 無音フィクスチャを らじれこ風の {局}/{番組}/ に置く。differentAudio なら音声区間を 1 バイト変えて別放送にする。
    private void PlaceSource(string fileName, bool differentAudio = false)
    {
        var dir = Path.Combine(RadioRoot, Station, Program);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silent.m4a"), path);
        if (!differentAudio)
            return;

        long middle;
        using (var file = TagLib.File.Create(path))
            middle = (file.InvariantStartPosition + file.InvariantEndPosition) / 2;
        var bytes = File.ReadAllBytes(path);
        bytes[middle] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    // 台帳ファイルを名前で分ける。新しい名前 = 台帳を失った状態。
    private SqliteLedger Ledger(string name)
    {
        var ledger = new SqliteLedger(Path.Combine(_work, name + ".db"));
        _ledgers.Add(ledger);
        return ledger;
    }

    private IngestService Build(ILedger ledger)
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
                new StationResolver(new StationMasterConfig()),
                new ProgramResolver(new ProgramMasterConfig())),
            new SiblingFolderDetector(new ProgramResolver(new ProgramMasterConfig()), new RajirekoImporter()),
            new LocalFolderTarget(fileSystem, new TagLibTagWriter(), new TagLibAudioContentComparer(), OutputRoot),
            ledger,
            TimeProvider.System,
            genre: "ラジオ",
            new NullLibraryRefresher(),
            new NullIngestSummaryPublisher(),
            new NullIngestFailureStore(),
            new NullIngestFailurePublisher());
    }

    private string[] OutputFiles() => Directory.GetFiles(OutputDir).Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public async Task LedgerLost_Rescan_SameContent_IsAlreadyImported_AndLedgerSelfHeals()
    {
        // #79 の再現: 取込済みの回を、台帳を失った状態で再走査する。
        PlaceSource(At0830);
        var first = await Build(Ledger("before")).RunOnceAsync(dryRun: false);
        Assert.Equal(IngestOutcome.Imported, Assert.Single(first.Items).Outcome);

        var fresh = Ledger("after");
        var report = await Build(fresh).RunOnceAsync(dryRun: false);

        var item = Assert.Single(report.Items);
        Assert.Equal(IngestOutcome.SkippedAlreadyImported, item.Outcome);
        Assert.Equal(BaseOutput, item.OutputPath);
        Assert.Equal([$"{Program} 2024-12-23.m4a"], OutputFiles()); // ` 0830` を作らない

        // 新しい台帳に記録される(出力パスは既存ファイル)。次の走査は台帳だけで済む。
        var entry = Assert.Single(fresh.ListAll());
        Assert.Equal(BaseOutput, entry.OutputPath);
        Assert.True(fresh.Contains(item.Episode!.Identity, new TimeOnly(8, 30), 0));
    }

    [Fact]
    public async Task SameDay_DifferentTime_DifferentContent_BothImported_WithTimeSuffix()
    {
        // 回帰(ADR-0002): 同日・別時刻で内容の違う放送は、従来どおり ` HHmm` で両方残す。
        var ledger = Ledger("ledger");
        PlaceSource(At0830);
        await Build(ledger).RunOnceAsync(dryRun: false);

        PlaceSource(At1000, differentAudio: true);
        var report = await Build(ledger).RunOnceAsync(dryRun: false);

        var imported = Assert.Single(report.Items, i => i.Outcome == IngestOutcome.Imported);
        Assert.Equal(Path.Combine(OutputDir, $"{Program} 2024-12-23 1000.m4a"), imported.OutputPath);
        Assert.NotNull(imported.Message); // 別名にしたことを警告として残す(常駐は Warning ログ)
        Assert.Equal([$"{Program} 2024-12-23 1000.m4a", $"{Program} 2024-12-23.m4a"], OutputFiles());
    }

    [Fact]
    public async Task ExistingTimeSuffixedNearDup_SameContent_DoesNotCreateThird()
    {
        // 10:00 の回がベース名、08:30 の回が ` 0830` に入った状態から台帳を失って再走査する。
        // 08:30 はベース名とは違う内容だが ` 0830` と同じ内容 → 3 つ目を作らない。
        var first = Ledger("first");
        PlaceSource(At1000, differentAudio: true);
        await Build(first).RunOnceAsync(dryRun: false);
        PlaceSource(At0830);
        await Build(first).RunOnceAsync(dryRun: false);
        Assert.Equal([$"{Program} 2024-12-23 0830.m4a", $"{Program} 2024-12-23.m4a"], OutputFiles());

        var report = await Build(Ledger("lost")).RunOnceAsync(dryRun: false);

        Assert.All(report.Items, i => Assert.Equal(IngestOutcome.SkippedAlreadyImported, i.Outcome));
        Assert.Equal(2, report.Items.Count);
        Assert.Contains(report.Items, i => i.OutputPath == Path.Combine(OutputDir, $"{Program} 2024-12-23 0830.m4a"));
        Assert.Equal([$"{Program} 2024-12-23 0830.m4a", $"{Program} 2024-12-23.m4a"], OutputFiles());
    }

    [Fact]
    public async Task ComparisonFailure_IsTreatedAsDifferent_AndWritten()
    {
        // 既存のベース名が読めない(TagLib が解釈できない)→ 判定失敗 → 違う内容として書く(欠落 > 重複)。
        Directory.CreateDirectory(OutputDir);
        File.WriteAllText(BaseOutput, "not an mp4");
        PlaceSource(At0830);

        var report = await Build(Ledger("ledger")).RunOnceAsync(dryRun: false);

        var item = Assert.Single(report.Items);
        Assert.Equal(IngestOutcome.Imported, item.Outcome);
        Assert.Equal(Path.Combine(OutputDir, $"{Program} 2024-12-23 0830.m4a"), item.OutputPath);
        Assert.Equal("not an mp4", File.ReadAllText(BaseOutput)); // 既存は触らない
    }

    [Fact]
    public async Task DryRun_SameContent_IsAlreadyImported_WithoutSideEffects()
    {
        // dry-run でも同じ判定で `済` と出す。台帳が無い(NullLedger)状態で、書き込みも記録もしない。
        PlaceSource(At0830);
        await Build(Ledger("before")).RunOnceAsync(dryRun: false);

        var report = await Build(new NullLedger()).RunOnceAsync(dryRun: true);

        var item = Assert.Single(report.Items);
        Assert.Equal(IngestOutcome.SkippedAlreadyImported, item.Outcome);
        Assert.Equal(BaseOutput, item.OutputPath);
        Assert.Equal([$"{Program} 2024-12-23.m4a"], OutputFiles());
    }
}
