using Airshelf.Core.Ledger;
using Airshelf.Gui.Core;

namespace Airshelf.Tests;

public sealed class IngestHistoryRowTests
{
    [Fact]
    public void FormatBroadcast_date_only_when_no_time_no_part()
    {
        var s = IngestHistoryRow.FormatBroadcast(new DateOnly(2026, 6, 12), time: null, partIndex: 0);
        Assert.Equal("2026-06-12", s);
    }

    [Fact]
    public void FormatBroadcast_includes_time_when_present()
    {
        var s = IngestHistoryRow.FormatBroadcast(new DateOnly(2026, 6, 12), new TimeOnly(8, 30), partIndex: 0);
        Assert.Equal("2026-06-12 08:30", s);
    }

    [Fact]
    public void FormatBroadcast_includes_part_when_positive()
    {
        // 時刻なし・同日別放送(radikool)は part で区別する。
        var s = IngestHistoryRow.FormatBroadcast(new DateOnly(2026, 6, 12), time: null, partIndex: 2);
        Assert.Equal("2026-06-12 (2)", s);
    }

    [Fact]
    public void From_copies_fields_and_formats_broadcast()
    {
        var row = IngestHistoryRow.From(Entry());

        Assert.Equal("オールナイトニッポン0", row.Program);
        Assert.Equal("J-WAVE", row.Station);
        Assert.Equal("2026-06-12 03:00", row.Broadcast);
        Assert.Equal(@"\\nas\share\radio\ann0.m4a", row.OutputPath);
        Assert.False(string.IsNullOrEmpty(row.ImportedAt)); // ローカル時刻整形(TZ 依存なので値は固定しない)
    }

    [Fact]
    public void From_carries_source_path_for_reveal()
    {
        // 取込元は列に出さないが、右クリック「取込元を開く」と詳細ペインが使うため行に運ぶ(#87)。
        var row = IngestHistoryRow.From(Entry());

        Assert.Equal(@"I:\radio\らじれこ\a.m4a", row.SourcePath);
    }

    [Fact]
    public void FileName_shows_only_the_output_file_name()
    {
        // グリッド列はファイル名のみ。UNC でも Windows パスの区切りで分解できること(OS を問わず走る)。
        var row = IngestHistoryRow.From(Entry());

        Assert.Equal("ann0.m4a", row.FileName);
    }

    private static LedgerEntry Entry() => new()
    {
        IdentityKey = "k",
        Station = "J-WAVE",
        Program = "オールナイトニッポン0",
        BroadcastDate = new DateOnly(2026, 6, 12),
        BroadcastTime = new TimeOnly(3, 0),
        PartIndex = 0,
        SourcePath = @"I:\radio\らじれこ\a.m4a",
        OutputPath = @"\\nas\share\radio\ann0.m4a",
        RuleVersion = "test",
        UsedFallback = false,
        ImportedAtUtc = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
    };
}
