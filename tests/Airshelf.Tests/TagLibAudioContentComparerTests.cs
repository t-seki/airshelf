using Airshelf.Core.Model;
using Airshelf.Core.Output;

namespace Airshelf.Tests;

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

    // ---- MP4 のトップレベル構成(ADR-0015)----

    // トップレベルのボックス(size + type + payload)を組み立てる。
    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    // silent.m4a(ftyp / free / mdat / moov)の既存 mdat の直前に、トップレベルのボックスを差し込む。
    private string WithBoxBeforeMdat(string name, byte[] box)
    {
        var bytes = File.ReadAllBytes(Fixture("silent.m4a"));
        const int mdatOffset = 36; // フィクスチャの構成(ftyp 28 + free 8)
        Assert.Equal("mdat", System.Text.Encoding.ASCII.GetString(bytes, mdatOffset + 4, 4));

        var path = Path.Combine(_work, name);
        File.WriteAllBytes(path, [.. bytes[..mdatOffset], .. box, .. bytes[mdatOffset..]]);
        return path;
    }

    private static byte[] InvariantBytes(string path)
    {
        using var file = TagLib.File.Create(path);
        return File.ReadAllBytes(path)[(int)file.InvariantStartPosition..(int)file.InvariantEndPosition];
    }

    [Fact]
    public void Mp4_WithTwoMdat_WhereOnlyLastMatches_IsNotSame()
    {
        // 前の mdat(番組本編に相当)が違い、最後の mdat(末尾の無音に相当)だけが同じ 2 ファイル。
        var a = WithBoxBeforeMdat("a.m4a", Box("mdat", Enumerable.Repeat((byte)0xAA, 64).ToArray()));
        var b = WithBoxBeforeMdat("b.m4a", Box("mdat", Enumerable.Repeat((byte)0xBB, 64).ToArray()));

        // 前提: TagLib の不変区間は最後の mdat しか指さないので、区間だけ見ると一致してしまう。
        Assert.Equal(InvariantBytes(a), InvariantBytes(b));

        Assert.False(_sut.HasSameAudio(a, b));
    }

    [Fact]
    public void Mp4_Fragmented_WithMoof_IsNotSame_EvenIfIdentical()
    {
        // fragmented MP4(moof あり)は判定できない = 違う内容扱い。中身が同一でも同じとは言わない。
        var box = Box("moof", new byte[16]);
        var a = WithBoxBeforeMdat("a.m4a", box);
        var b = WithBoxBeforeMdat("b.m4a", box);

        Assert.False(_sut.HasSameAudio(a, b));
    }

    [Fact]
    public void Mp4_WithBrokenTopLevelBox_IsNotSame()
    {
        // トップレベルのボックスがファイル末尾を越える size を名乗る(壊れたヘッダ)。
        var broken = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(broken, 0x7FFF_FFFF);
        System.Text.Encoding.ASCII.GetBytes("free").CopyTo(broken, 4);
        var a = Path.Combine(_work, "a.m4a");
        var b = Path.Combine(_work, "b.m4a");
        File.WriteAllBytes(a, [.. File.ReadAllBytes(Fixture("silent.m4a")), .. broken]);
        File.Copy(a, b);

        Assert.False(_sut.HasSameAudio(a, b));
    }

    [Fact]
    public void Mp4_WithLargeSizeMdat_IsStillCompared()
    {
        // 64bit largesize(size == 1)の mdat 1 つでも、ボックスを辿れて比較の対象になる。
        var bytes = File.ReadAllBytes(Fixture("silent.m4a"));
        const int mdatOffset = 36;
        var mdatSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mdatOffset));
        var large = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(large, 1);
        System.Text.Encoding.ASCII.GetBytes("mdat").CopyTo(large, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(large.AsSpan(8), (ulong)mdatSize + 8);
        var a = Path.Combine(_work, "a.m4a");
        var b = Path.Combine(_work, "b.m4a");
        File.WriteAllBytes(a,
            [.. bytes[..mdatOffset], .. large, .. bytes[(mdatOffset + 8)..(mdatOffset + mdatSize)], .. bytes[(mdatOffset + mdatSize)..]]);
        File.Copy(a, b);

        Assert.True(_sut.HasSameAudio(a, b));
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
