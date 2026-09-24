using Radirec.Core.Model;
using Radirec.Core.Output;

namespace Radirec.Tests;

/// <summary>
/// 出力先の衝突判定(ADR-0015)の前提を実ファイルで確かめる: <see cref="TagLibTagWriter"/> でタグを書き換えた
/// 出力とソースで、TagLib の不変区間(タグ以外の音声部分)が一致すること。MP4(らじれこ)と MP3(radikool)の両方。
/// </summary>
public sealed class TagLibAudioContentComparerTests : IDisposable
{
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "radirec-test-" + Guid.NewGuid().ToString("N"));

    private readonly TagLibAudioContentComparer _sut = new();

    public TagLibAudioContentComparerTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        if (Directory.Exists(_work))
            Directory.Delete(_work, recursive: true);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private string Copy(string fixture, string name)
    {
        var target = Path.Combine(_work, name);
        File.Copy(Fixture(fixture), target);
        return target;
    }

    // 元のタグより長い値を入れて、タグ領域のサイズ(= 音声区間の位置)が変わるようにする。
    private static NormalizedTags Tags() => new()
    {
        AlbumArtist = "文化放送",
        Album = "タグ領域の大きさを変えるための長い番組名 — 日向坂46の「ひ」",
        Artist = "日向坂46 とゲストの長い出演者名",
        Title = "2020-07-12",
        Date = new DateOnly(2020, 7, 12),
        Disc = 2020,
        Track = 712,
        Genre = "ラジオ",
        Comment = null,
    };

    [Theory]
    [InlineData("silent.m4a")]
    [InlineData("radikool-sample.mp3")]
    public void TagRewrittenOutput_HasSameAudio_AsSource(string fixture)
    {
        var output = Copy(fixture, "out" + Path.GetExtension(fixture));
        new TagLibTagWriter().Write(output, Tags());

        // タグを書き換えたのでファイル全体は一致しない(サイズ比較・全体比較では判定できない)。
        Assert.NotEqual(File.ReadAllBytes(Fixture(fixture)), File.ReadAllBytes(output));
        Assert.True(_sut.HasSameAudio(Fixture(fixture), output));
    }

    [Theory]
    [InlineData("silent.m4a")]
    [InlineData("radikool-sample.mp3")]
    public void DifferentAudio_IsNotSame(string fixture)
    {
        var other = Copy(fixture, "other" + Path.GetExtension(fixture));
        long middle;
        using (var file = TagLib.File.Create(other))
            middle = (file.InvariantStartPosition + file.InvariantEndPosition) / 2;

        // 音声区間の中ほどを 1 バイト変える(タグは同じ)。
        var bytes = File.ReadAllBytes(other);
        bytes[middle] ^= 0xFF;
        File.WriteAllBytes(other, bytes);

        Assert.False(_sut.HasSameAudio(Fixture(fixture), other));
    }

    [Fact]
    public void Unreadable_IsNotSame_WithoutThrowing()
    {
        // 判定失敗は例外にせず「違う内容」(欠落 > 重複)。壊れたファイルと存在しないファイル。
        var garbage = Path.Combine(_work, "garbage.m4a");
        File.WriteAllText(garbage, "not an mp4");

        Assert.False(_sut.HasSameAudio(Fixture("silent.m4a"), garbage));
        Assert.False(_sut.HasSameAudio(Fixture("silent.m4a"), Path.Combine(_work, "missing.m4a")));
    }
}
