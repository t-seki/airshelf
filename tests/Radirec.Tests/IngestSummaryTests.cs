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
        Assert.Equal(2, a.EpisodeCount);
        Assert.Equal(0, a.FirstEpisode.PartIndex);          // part 無しは 0 に正規化
        Assert.Equal(new DateOnly(2026, 1, 1), a.FirstEpisode.BroadcastDate);  // 先頭 = 出現順の 1 件目
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
    public void 番組内の放送回は何件でも実数を保つ()
    {
        // かつては 1 番組 20 回で打ち切り、表示が「20件」と嘘をついていた(#100)。
        // 番組数の上限(MaxAlbums)と違い、**放送回の件数は打ち切らない**。
        const int many = 25;
        var episodes = Enumerable.Range(1, many).Select(i => Ep("局A", "番組A", i)).ToList();

        var summary = IngestSummary.From(episodes);

        var album = Assert.Single(summary.Albums);
        Assert.Equal(many, album.EpisodeCount);        // 表示に出る件数 = 実数
        Assert.Equal(many, summary.TotalImported);
        Assert.False(summary.Truncated);               // 番組は 1 つなので間引いていない
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
