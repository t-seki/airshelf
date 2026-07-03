using Radirec.Core.Model;
using Radirec.Core.Normalization;

namespace Radirec.Tests;

public sealed class TagMapperTests
{
    private static Episode Episode(
        int year = 2026, int month = 6, int day = 12,
        string station = "J-WAVE", string program = "LOGISTEED RADIONOMICS",
        string? artist = "鶴崎仁香（日向坂46）", string? kind = "TimeFree", int? partIndex = null)
        => new()
        {
            Identity = new IdentityKey(station, program, new DateOnly(year, month, day)),
            BroadcastTime = new TimeOnly(22, 30, 0),
            Artist = artist,
            RecordingKind = kind,
            PartIndex = partIndex,
        };

    [Fact]
    public void Map_StandardEpisode_ProducesCaseAFields()
    {
        var tags = TagMapper.Map(Episode());

        Assert.Equal("J-WAVE", tags.AlbumArtist);
        Assert.Equal("LOGISTEED RADIONOMICS", tags.Album);
        Assert.Equal("鶴崎仁香（日向坂46）", tags.Artist);
        Assert.Equal("2026-06-12", tags.Title);
        Assert.Equal(new DateOnly(2026, 6, 12), tags.Date);
        Assert.Equal((ushort)2026, tags.Disc);
        Assert.Equal((ushort)612, tags.Track);
        Assert.Equal("ラジオ", tags.Genre);
        Assert.Equal("TimeFree", tags.Comment);
    }

    [Theory]
    [InlineData(2026, 1, 5, 105)]    // 1/5
    [InlineData(2026, 6, 12, 612)]   // 6/12
    [InlineData(2026, 12, 31, 1231)] // 12/31
    [InlineData(2024, 2, 29, 229)]   // 閏日
    public void Map_Track_IsMonthTimes100PlusDay(int y, int m, int d, int expectedTrack)
    {
        var tags = TagMapper.Map(Episode(y, m, d));

        Assert.Equal((ushort)expectedTrack, tags.Track);
        Assert.Equal((ushort)y, tags.Disc);
    }

    [Fact]
    public void Map_TitleIsZeroPaddedIso()
    {
        var tags = TagMapper.Map(Episode(2026, 3, 5));

        Assert.Equal("2026-03-05", tags.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Map_BlankArtist_BecomesNull(string? artist)
    {
        var tags = TagMapper.Map(Episode(artist: artist));

        Assert.Null(tags.Artist);
    }

    [Fact]
    public void Map_BlankRecordingKind_BecomesNullComment()
    {
        var tags = TagMapper.Map(Episode(kind: null));

        Assert.Null(tags.Comment);
    }

    [Theory]
    // 同日 part1/part2 が track で part1 < part2 の順、title にパートが付く
    [InlineData(1, 6121, "2026-06-12 (1)")]
    [InlineData(2, 6122, "2026-06-12 (2)")]
    public void Map_WithPartIndex_SubdividesTrackAndTitle(int part, int expectedTrack, string expectedTitle)
    {
        var tags = TagMapper.Map(Episode(2026, 6, 12, partIndex: part));

        Assert.Equal((ushort)expectedTrack, tags.Track); // MMDD*10 + part(612*10+part)
        Assert.Equal(expectedTitle, tags.Title);
    }

    [Fact]
    public void Map_WithoutPartIndex_TrackIsMmddAndTitleIsDate()
    {
        var tags = TagMapper.Map(Episode(2026, 6, 12));

        Assert.Equal((ushort)612, tags.Track);
        Assert.Equal("2026-06-12", tags.Title);
    }

    [Fact]
    public void Map_CustomGenre_IsHonored()
    {
        var tags = TagMapper.Map(Episode(), genre: "ラジオドラマ");

        Assert.Equal("ラジオドラマ", tags.Genre);
    }
}
