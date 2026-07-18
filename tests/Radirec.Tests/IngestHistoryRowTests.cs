using Radirec.Core.Ledger;
using Radirec.Gui.Core;

namespace Radirec.Tests;

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
        var entry = new LedgerEntry
        {
            IdentityKey = "k",
            Station = "J-WAVE",
            Program = "オールナイトニッポン0",
            BroadcastDate = new DateOnly(2026, 6, 12),
            BroadcastTime = new TimeOnly(3, 0),
            PartIndex = 0,
            SourcePath = @"I:\radio\a.m4a",
            OutputPath = @"\\nas\share\radio\ann0.m4a",
            RuleVersion = "test",
            UsedFallback = false,
            ImportedAtUtc = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
        };

        var row = IngestHistoryRow.From(entry);

        Assert.Equal("オールナイトニッポン0", row.Program);
        Assert.Equal("J-WAVE", row.Station);
        Assert.Equal("2026-06-12 03:00", row.Broadcast);
        Assert.Equal(@"\\nas\share\radio\ann0.m4a", row.Output);
        Assert.False(string.IsNullOrEmpty(row.ImportedAt)); // ローカル時刻整形(TZ 依存なので値は固定しない)
    }
}
