using Airshelf.Core.Model;
using Airshelf.Core.Output;

namespace Airshelf.Tests;

/// <summary>
/// TagLib# 書込スパイク: 実 M4A(無音フィクスチャ)へ案A タグを書き、読み戻して検証する統合テスト。
/// 特に date のフル ISO(©day)が保持されることを確認する。
/// </summary>
public sealed class TagLibTagWriterTests : IDisposable
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "silent.m4a");

    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "airshelf-test-" + Guid.NewGuid().ToString("N"));

    public TagLibTagWriterTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        if (Directory.Exists(_work))
            Directory.Delete(_work, recursive: true);
    }

    private string CopyFixture(string name = "out.m4a")
    {
        var target = Path.Combine(_work, name);
        File.Copy(FixturePath, target);
        return target;
    }

    [Fact]
    public void Write_RoundTripsAllCaseAFields()
    {
        var target = CopyFixture();
        var tags = new NormalizedTags
        {
            AlbumArtist = "J-WAVE",
            Album = "LOGISTEED RADIONOMICS",
            Artist = "鶴崎仁香（日向坂46）",
            Title = "2026-06-12",
            Date = new DateOnly(2026, 6, 12),
            Disc = 2026,
            Track = 612,
            Genre = "ラジオ",
            Comment = "TimeFree",
        };

        new TagLibTagWriter().Write(target, tags);

        using var file = TagLib.File.Create(target);
        var t = file.Tag;
        Assert.Equal("LOGISTEED RADIONOMICS", t.Album);
        Assert.Equal("J-WAVE", t.FirstAlbumArtist);
        Assert.Equal("鶴崎仁香（日向坂46）", t.FirstPerformer);
        Assert.Equal("2026-06-12", t.Title);
        Assert.Equal("ラジオ", t.FirstGenre);
        Assert.Equal("TimeFree", t.Comment);
        Assert.Equal(2026u, t.Disc);
        Assert.Equal(612u, t.Track);
        // フル ISO 日付(©day)が保持される
        Assert.Equal("2026-06-12", TagLibTagWriter.ReadIsoDate(target));
    }

    // ---- MP3 / ID3(radikool)----

    private string CopyMp3Fixture(string name = "out.mp3")
    {
        var target = Path.Combine(_work, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "radikool-sample.mp3"), target);
        return target;
    }

    [Fact]
    public void Write_Mp3_RoundTripsCaseAFields_AndClearsSourceComment()
    {
        // radikool 相当(元 comment="2020年07月12日", genre="文化放送")に案A を上書きする。
        var target = CopyMp3Fixture();
        var tags = new NormalizedTags
        {
            AlbumArtist = "文化放送",
            Album = "日向坂46の「ひ」",
            Artist = "日向坂46",
            Title = "2020-07-12",
            Date = new DateOnly(2020, 7, 12),
            Disc = 2020,
            Track = 712,
            Genre = "ラジオ",
            Comment = null, // radikool は録音種別なし → comment は空(元コメントを残さない)
        };

        new TagLibTagWriter().Write(target, tags);

        using var file = TagLib.File.Create(target);
        var t = file.Tag;
        Assert.Equal("日向坂46の「ひ」", t.Album);
        Assert.Equal("文化放送", t.FirstAlbumArtist);
        Assert.Equal("日向坂46", t.FirstPerformer);
        Assert.Equal("2020-07-12", t.Title);
        Assert.Equal("ラジオ", t.FirstGenre);
        Assert.Equal(2020u, t.Disc);
        Assert.Equal(712u, t.Track);
        // 元ソースの comment("2020年07月12日")が残っていないこと。
        Assert.True(string.IsNullOrEmpty(t.Comment), $"comment が残存: '{t.Comment}'");
        // ID3v2 TDRC にフル日付が入ること(Tag.Year の年潰しを避けた確認)。
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2);
        Assert.Equal("2020-07-12", id3.GetTextAsString("TDRC"));
    }

    [Fact]
    public void Write_NullArtistAndComment_AreNotSet()
    {
        var target = CopyFixture("noartist.m4a");
        var tags = new NormalizedTags
        {
            AlbumArtist = "NHKラジオ第1",
            Album = "番組",
            Artist = null,
            Title = "2024-11-11",
            Date = new DateOnly(2024, 11, 11),
            Disc = 2024,
            Track = 1111,
            Genre = "ラジオ",
            Comment = null,
        };

        new TagLibTagWriter().Write(target, tags);

        using var file = TagLib.File.Create(target);
        var t = file.Tag;
        Assert.True(string.IsNullOrEmpty(t.FirstPerformer));
        Assert.True(string.IsNullOrEmpty(t.Comment));
        Assert.Equal("番組", t.Album);
        Assert.Equal(1111u, t.Track);
    }
}
