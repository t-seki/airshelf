using System.IO.Abstractions.TestingHelpers;
using Radirec.Core.Model;
using Radirec.Core.Output;

namespace Radirec.Tests;

public sealed class LocalFolderTargetTests
{
    private sealed class FakeTagWriter : ITagWriter
    {
        public List<string> Written { get; } = [];
        public void Write(string filePath, NormalizedTags tags) => Written.Add(filePath);
    }

    private const string Root = "/out";
    private const string SourcePath = "/in/src.m4a";

    private static RecordingFile Source() => new()
    {
        FilePath = SourcePath,
        FileName = "src.m4a",
        StationFolder = "J-WAVE",
        ProgramFolder = "LOGISTEEDRADIONOMICS",
        Extension = ".m4a",
    };

    private static Episode Episode(TimeOnly? time = null, int? partIndex = null) => new()
    {
        Identity = new IdentityKey("J-WAVE", "LOGISTEED RADIONOMICS", new DateOnly(2026, 6, 12)),
        BroadcastTime = time,
        PartIndex = partIndex,
    };

    private static NormalizedTags Tags() => new()
    {
        AlbumArtist = "J-WAVE",
        Album = "LOGISTEED RADIONOMICS",
        Title = "2026-06-12",
        Date = new DateOnly(2026, 6, 12),
        Disc = 2026,
        Track = 612,
        Genre = "ラジオ",
    };

    [Fact]
    public void PlanPath_UsesStationProgramAndDate()
    {
        var fs = new MockFileSystem();
        var sut = new LocalFolderTarget(fs, new FakeTagWriter(), Root);

        var path = sut.PlanPath(Source(), Episode(), Tags());

        Assert.Equal(
            fs.Path.Combine(Root, "J-WAVE", "LOGISTEED RADIONOMICS", "LOGISTEED RADIONOMICS 2026-06-12.m4a"),
            path);
    }

    [Fact]
    public async Task WriteAsync_CopiesTagsAndFinalizes_WithoutTouchingSource()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [SourcePath] = new MockFileData("audio-bytes"),
        });
        var tagWriter = new FakeTagWriter();
        var sut = new LocalFolderTarget(fs, tagWriter, Root);

        var final = await sut.WriteAsync(Source(), Episode(), Tags());

        var partial = final[..^".m4a".Length] + ".partial.m4a"; // 拡張子を保った .partial 名
        Assert.True(fs.File.Exists(final));                    // 最終ファイルができる
        Assert.False(fs.File.Exists(partial));                 // .partial は残らない
        Assert.True(fs.File.Exists(SourcePath));               // 原本は非破壊
        Assert.Contains(partial, tagWriter.Written);           // タグはリネーム前(.partial)に書く
        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12.m4a", final);
    }

    [Fact]
    public async Task WriteAsync_WithPartIndex_AppendsPartSuffix()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [SourcePath] = new MockFileData("audio-bytes"),
        });
        var sut = new LocalFolderTarget(fs, new FakeTagWriter(), Root);

        // 同一アルバム・同一日でも由来フォルダのパート番号で別ファイルになる。
        var p1 = await sut.WriteAsync(Source(), Episode(partIndex: 1), Tags());
        var p2 = await sut.WriteAsync(Source(), Episode(partIndex: 2), Tags());

        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12-1.m4a", p1);
        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12-2.m4a", p2);
    }

    [Fact]
    public async Task WriteAsync_OnCollision_AppendsTime()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [SourcePath] = new MockFileData("audio-bytes"),
        });
        var sut = new LocalFolderTarget(fs, new FakeTagWriter(), Root);

        // 既に同名(同日)が存在する状況を作る
        var first = await sut.WriteAsync(Source(), Episode(new TimeOnly(22, 30)), Tags());
        var second = await sut.WriteAsync(Source(), Episode(new TimeOnly(1, 5)), Tags());

        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12.m4a", first);
        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12 0105.m4a", second);
    }
}
