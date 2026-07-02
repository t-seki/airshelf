using Radirec.Core.Model;
using Radirec.Core.Output;

namespace Radirec.Tests;

/// <summary>
/// TagLib# 書込スパイク: 実 M4A(無音フィクスチャ)へ案A タグを書き、読み戻して検証する統合テスト。
/// 特に date のフル ISO(©day)が保持されることを確認する。
/// </summary>
public sealed class TagLibTagWriterTests : IDisposable
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "silent.m4a");

    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "radirec-test-" + Guid.NewGuid().ToString("N"));

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
