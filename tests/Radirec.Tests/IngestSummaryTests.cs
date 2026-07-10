using Radirec.Core.Ingestion;
using Radirec.Core.Model;

namespace Radirec.Tests;

public sealed class IngestSummaryTests
{
    private static Episode Ep(string station, string program, int day, int? part = null) => new()
    {
        Identity = new IdentityKey(station, program, new DateOnly(2026, 1, day)),
        PartIndex = part,
    };

    [Fact]
    public void 番組単位でグループしTotalとPartを保つ()
    {
        var summary = IngestSummary.From(
        [
            Ep("局A", "番組A", 1),
            Ep("局A", "番組A", 2, part: 2),
            Ep("局B", "番組B", 1),
        ]);

        Assert.Equal(3, summary.TotalImported);
        Assert.False(summary.Truncated);
        Assert.Equal(2, summary.Albums.Count);

        var a = summary.Albums[0];
        Assert.Equal("局A", a.Station);
        Assert.Equal("番組A", a.Program);
        Assert.Equal(2, a.Episodes.Count);
        Assert.Equal(0, a.Episodes[0].PartIndex);   // part 無しは 0 に正規化
        Assert.Equal(2, a.Episodes[1].PartIndex);
    }

    [Fact]
    public void 出現順を保つ()
    {
        var summary = IngestSummary.From([Ep("局Z", "後", 1), Ep("局A", "先", 1)]);

        Assert.Equal("後", summary.Albums[0].Program);
        Assert.Equal("先", summary.Albums[1].Program);
    }

    [Fact]
    public void 番組数が上限超過でTruncated_総数は実数を保つ()
    {
        var episodes = Enumerable.Range(1, IngestSummary.MaxAlbums + 5)
            .Select(i => Ep($"局{i}", $"番組{i}", 1))
            .ToList();

        var summary = IngestSummary.From(episodes);

        Assert.True(summary.Truncated);
        Assert.Equal(IngestSummary.MaxAlbums, summary.Albums.Count);
        Assert.Equal(IngestSummary.MaxAlbums + 5, summary.TotalImported);
    }

    [Fact]
    public void 番組内の放送回が上限超過でTruncated()
    {
        // 同一番組で放送回だけが上限超過(日付をずらして別放送回にする)。
        var episodes = Enumerable.Range(1, IngestSummary.MaxEpisodesPerAlbum + 3)
            .Select(i => Ep("局A", "番組A", i))
            .ToList();

        var summary = IngestSummary.From(episodes);

        Assert.True(summary.Truncated);
        var album = Assert.Single(summary.Albums);
        Assert.Equal(IngestSummary.MaxEpisodesPerAlbum, album.Episodes.Count);
        Assert.Equal(IngestSummary.MaxEpisodesPerAlbum + 3, summary.TotalImported);
    }

    [Fact]
    public void 空入力は空要約()
    {
        var summary = IngestSummary.From([]);

        Assert.Equal(0, summary.TotalImported);
        Assert.False(summary.Truncated);
        Assert.Empty(summary.Albums);
    }
}
