using Radirec.Gui.Core;

namespace Radirec.Tests;

public sealed class IngestToastFormatterTests
{
    private static IngestSummaryPayload Payload(
        int total, bool truncated, params IngestSummaryAlbumPayload[] albums) => new()
    {
        SchemaVersion = 1,
        TotalImported = total,
        Truncated = truncated,
        Albums = albums,
    };

    private static IngestSummaryAlbumPayload Album(
        string station, string program, params IngestSummaryEpisodePayload[] episodes) =>
        new() { Station = station, Program = program, Episodes = episodes };

    private static IngestSummaryEpisodePayload Ep(string date, string? time = null, int part = 0) =>
        new() { BroadcastDate = date, BroadcastTime = time, PartIndex = part };

    [Fact]
    public void 単一番組単一回は放送日時を出す()
    {
        var (title, body) = IngestToastFormatter.Format(
            Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-10", "22:30"))));

        Assert.Equal("新規取込 1件", title);
        Assert.Equal("番組A（J-WAVE）2026-07-10 22:30", body);
    }

    [Fact]
    public void 放送時刻無しは日付のみ()
    {
        var (_, body) = IngestToastFormatter.Format(
            Payload(1, false, Album("局", "番組", Ep("2026-01-01"))));

        Assert.Equal("番組（局）2026-01-01", body);
    }

    [Fact]
    public void 同一番組複数回は件数表記()
    {
        var (_, body) = IngestToastFormatter.Format(
            Payload(2, false, Album("TBS", "番組", Ep("2026-01-01", "08:30"), Ep("2026-01-01", "10:00"))));

        Assert.Equal("番組（TBS）2件", body);
    }

    [Fact]
    public void 複数番組は行で並べる()
    {
        var (title, body) = IngestToastFormatter.Format(Payload(2, false,
            Album("局1", "番組1", Ep("2026-01-01", "10:00")),
            Album("局2", "番組2", Ep("2026-01-02", "11:00"))));

        Assert.Equal("新規取込 2件", title);
        Assert.Equal("番組1（局1）2026-01-01 10:00\n番組2（局2）2026-01-02 11:00", body);
    }

    [Fact]
    public void 上限超過はほかに畳む()
    {
        var albums = Enumerable.Range(1, IngestToastFormatter.MaxLines + 2)
            .Select(i => Album($"局{i}", $"番組{i}", Ep("2026-01-01", "10:00")))
            .ToArray();

        var (_, body) = IngestToastFormatter.Format(Payload(albums.Length, false, albums));

        var lines = body.Split('\n');
        Assert.Equal(IngestToastFormatter.MaxLines + 1, lines.Length); // 上限行 + 「…ほか」
        Assert.Equal("…ほか", lines[^1]);
    }

    [Fact]
    public void truncatedはほかを添える()
    {
        var (_, body) = IngestToastFormatter.Format(
            Payload(999, truncated: true, Album("局", "番組", Ep("2026-01-01", "10:00"))));

        Assert.EndsWith("…ほか", body);
    }
}
