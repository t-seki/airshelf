using System.IO.Abstractions.TestingHelpers;
using Airshelf.Core.Model;
using Airshelf.Core.Output;

namespace Airshelf.Tests;

public sealed class LocalFolderTargetTests
{
    private sealed class FakeTagWriter : ITagWriter
    {
        public List<string> Written { get; } = [];
        public void Write(string filePath, NormalizedTags tags) => Written.Add(filePath);
        public void WritePlacement(string filePath, NormalizedTags tags) => Written.Add(filePath);
    }

    // FakeTagWriter はバイトを変えないので、モック FS 上の全バイト比較で「音声部分の比較」を代用する。
    // 実物(TagLib の不変区間)は TagLibAudioContentComparerTests で確かめる。
    private sealed class FakeContentComparer(MockFileSystem fs) : IAudioContentComparer
    {
        public List<string> Compared { get; } = [];

        /// <summary>立てると判定失敗(実装は例外を握って false を返す契約)を模す。</summary>
        public bool Fails { get; set; }

        public bool HasSameAudio(string pathA, string pathB)
        {
            Compared.Add(pathB);
            return !Fails && fs.File.ReadAllBytes(pathA).AsSpan().SequenceEqual(fs.File.ReadAllBytes(pathB));
        }
    }

    private const string Root = "/out";
    private const string SourcePath = "/in/src.m4a";

    private static RecordingFile Source(string path = SourcePath) => new()
    {
        FilePath = path,
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

    private static LocalFolderTarget Target(MockFileSystem fs, ITagWriter? tagWriter = null, IAudioContentComparer? comparer = null)
        => new(fs, tagWriter ?? new FakeTagWriter(), comparer ?? new FakeContentComparer(fs), Root);

    // 取込と同じ順(配置先の算出 → 書き込み)で 1 回分を書く。
    private static Task<string> WriteAsync(LocalFolderTarget sut, Episode episode, RecordingFile? source = null)
    {
        var src = source ?? Source();
        return sut.WriteAsync(src, sut.Plan(src, episode, Tags()), Tags());
    }

    private static string OutDir(MockFileSystem fs) => fs.Path.Combine(Root, "J-WAVE", "LOGISTEED RADIONOMICS");

    [Fact]
    public void Plan_UsesStationProgramAndDate()
    {
        var fs = new MockFileSystem();
        var sut = Target(fs);

        var plan = sut.Plan(Source(), Episode(), Tags());

        Assert.Equal(
            fs.Path.Combine(Root, "J-WAVE", "LOGISTEED RADIONOMICS", "LOGISTEED RADIONOMICS 2026-06-12.m4a"),
            plan.Path);
        Assert.False(plan.AlreadyPresent);
        Assert.Null(plan.CollidedWith);
    }

    [Fact]
    public async Task WriteAsync_CopiesTagsAndFinalizes_WithoutTouchingSource()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [SourcePath] = new MockFileData("audio-bytes"),
        });
        var tagWriter = new FakeTagWriter();
        var sut = Target(fs, tagWriter);

        var final = await WriteAsync(sut, Episode());

        // .partial 名は製品と同じ Path 操作で組み立てる(OS のパス区切り差で崩れないように)。
        var partial = fs.Path.Combine(
            fs.Path.GetDirectoryName(final)!,
            fs.Path.GetFileNameWithoutExtension(final) + ".partial" + fs.Path.GetExtension(final));
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
        var comparer = new FakeContentComparer(fs);
        var sut = Target(fs, comparer: comparer);

        // 同一アルバム・同一日でも由来フォルダのパート番号で別ファイルになる。
        var p1 = await WriteAsync(sut, Episode(partIndex: 1));
        var p2 = await WriteAsync(sut, Episode(partIndex: 2));

        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12-1.m4a", p1);
        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12-2.m4a", p2);
        Assert.Empty(comparer.Compared); // 名前が衝突しなければ中身は読まない
    }

    // 置き場所のタグ書き込みを、ファイルの中身を書き換えることで模す(失敗も指定できる)。
    private sealed class RewritingTagWriter(MockFileSystem fs, bool fails = false) : ITagWriter
    {
        public List<string> Written { get; } = [];
        public void Write(string filePath, NormalizedTags tags) => Written.Add(filePath);

        public void WritePlacement(string filePath, NormalizedTags tags)
        {
            Written.Add(filePath);
            if (fails)
                throw new IOException("書き込みに失敗しました");
            fs.File.WriteAllText(filePath, "tagged");
        }
    }

    private const string InPlacePath = "/out/J-WAVE/LOGISTEED RADIONOMICS/LOGISTEED RADIONOMICS 2026-06-12.m4a";
    private const string InPlacePartial = "/out/J-WAVE/LOGISTEED RADIONOMICS/LOGISTEED RADIONOMICS 2026-06-12.partial.m4a";

    [Fact]
    public async Task RewritePlacement_WritesTagsOnPartial_AndReplacesTheFileInPlace()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [InPlacePath] = new("original"),
            [InPlacePartial] = new("leftover"), // 前回止まったときの書きかけ
        });
        var tagWriter = new RewritingTagWriter(fs);

        await Target(fs, tagWriter).RewritePlacementAsync(InPlacePath, Tags(), TestContext.Current.CancellationToken);

        Assert.Equal([fs.Path.GetFullPath(InPlacePartial)], tagWriter.Written.Select(fs.Path.GetFullPath)); // タグは .partial に書く
        Assert.Equal("tagged", fs.File.ReadAllText(InPlacePath)); // 同じ名前へ置き換わる
        Assert.False(fs.File.Exists(InPlacePartial));
        Assert.Single(fs.Directory.GetFiles(fs.Path.GetDirectoryName(InPlacePath)!));
    }

    [Fact]
    public async Task RewritePlacement_WhenTagWriteFails_KeepsTheOriginal_AndRemovesThePartial()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [InPlacePath] = new("original"),
        });

        await Assert.ThrowsAsync<IOException>(
            () => Target(fs, new RewritingTagWriter(fs, fails: true)).RewritePlacementAsync(InPlacePath, Tags(), TestContext.Current.CancellationToken));

        Assert.Equal("original", fs.File.ReadAllText(InPlacePath));
        Assert.False(fs.File.Exists(InPlacePartial));
    }

    // 取り込みのタグ書き込み(Write)が失敗する(らじれこの録音が壊れていて TagLib が読めない等)。
    private sealed class FailingTagWriter : ITagWriter
    {
        public void Write(string filePath, NormalizedTags tags) => throw new IOException("タグを書けません");
        public void WritePlacement(string filePath, NormalizedTags tags) => throw new IOException("タグを書けません");
    }

    [Fact]
    public async Task WriteAsync_WhenTagWriteFails_Throws_AndRemovesThePartial()
    {
        // 失敗した録音は走査のたびに再試行されるので、.partial を残すと起動時の掃除の後も出力先に居続ける(#249)。
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [SourcePath] = new MockFileData("broken-audio"),
        });
        var sut = Target(fs, new FailingTagWriter());

        await Assert.ThrowsAsync<IOException>(() => WriteAsync(sut, Episode()));

        Assert.Empty(fs.Directory.GetFiles(OutDir(fs)));
        Assert.True(fs.File.Exists(SourcePath)); // 取込元には触れない
    }

    [Fact]
    public async Task WriteAsync_WhenFinalRenameFails_Throws_AndRemovesThePartial_AndKeepsTheExistingFile()
    {
        // 配置先を決めた後に同じ名前のファイルが現れると、確定のリネームが失敗する(上書きしない)。
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [SourcePath] = new MockFileData("audio-mine"),
        });
        var sut = Target(fs);
        var plan = sut.Plan(Source(), Episode(), Tags());
        fs.AddFile(plan.Path, new MockFileData("appeared"));

        await Assert.ThrowsAnyAsync<IOException>(() => sut.WriteAsync(Source(), plan, Tags(), TestContext.Current.CancellationToken));

        Assert.Equal("appeared", fs.File.ReadAllText(plan.Path));
        Assert.Equal([fs.Path.GetFullPath(plan.Path)], fs.Directory.GetFiles(OutDir(fs)).Select(fs.Path.GetFullPath));
    }

    [Fact]
    public void CleanupOrphans_DeletesOnlyPartialTempFiles()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/out/J-WAVE/番組/回 2026-06-12.m4a"] = new("keep"),
            ["/out/J-WAVE/番組/回 2026-06-12.partial.m4a"] = new("orphan"),
            ["/out/J-WAVE/番組/回 2026-06-19.partial.m4a"] = new("orphan2"),
        });
        var sut = Target(fs);

        var removed = sut.CleanupOrphans();

        Assert.Equal(2, removed);
        Assert.True(fs.File.Exists("/out/J-WAVE/番組/回 2026-06-12.m4a"));      // 最終ファイルは残す
        Assert.False(fs.File.Exists("/out/J-WAVE/番組/回 2026-06-12.partial.m4a"));
        Assert.False(fs.File.Exists("/out/J-WAVE/番組/回 2026-06-19.partial.m4a"));
    }

    [Fact]
    public async Task WriteAsync_OnCollisionWithDifferentContent_AppendsTime()
    {
        // 同日・別時刻で内容の違う 2 放送(ADR-0002)。両方残す。
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [SourcePath] = new MockFileData("audio-2230"),
            ["/in/other.m4a"] = new MockFileData("audio-0105"),
        });
        var sut = Target(fs);

        var first = await WriteAsync(sut, Episode(new TimeOnly(22, 30)));
        var plan = sut.Plan(Source("/in/other.m4a"), Episode(new TimeOnly(1, 5)), Tags());
        var second = await sut.WriteAsync(Source("/in/other.m4a"), plan, Tags(), TestContext.Current.CancellationToken);

        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12.m4a", first);
        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12 0105.m4a", second);
        Assert.Equal(first, plan.CollidedWith); // 別名にしたことを呼び出し側へ知らせる(警告ログ用)
    }

    [Fact]
    public async Task Plan_OnCollisionWithSameContent_IsAlreadyPresent_AndWritesNothing()
    {
        // 台帳を失った後の再走査: 出力先に同じ回の取込済みファイルがある。
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [SourcePath] = new MockFileData("audio-bytes"),
        });
        var sut = Target(fs);
        var existing = await WriteAsync(sut, Episode(new TimeOnly(8, 30)));

        var plan = sut.Plan(Source(), Episode(new TimeOnly(8, 30)), Tags());

        Assert.True(plan.AlreadyPresent);
        Assert.Equal(existing, plan.Path);
        Assert.Single(fs.Directory.GetFiles(OutDir(fs))); // ` 0830` を作らない
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.WriteAsync(Source(), plan, Tags(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Plan_SameContentAsExistingTimeSuffixedFile_DoesNotCreateThird()
    {
        // 以前の実装が作った near-dup(` 0830`)が既にあり、ベース名は別内容。3 つ目を作らない。
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [SourcePath] = new MockFileData("audio-0830"),
            ["/out/J-WAVE/LOGISTEED RADIONOMICS/LOGISTEED RADIONOMICS 2026-06-12.m4a"] = new("audio-other"),
            ["/out/J-WAVE/LOGISTEED RADIONOMICS/LOGISTEED RADIONOMICS 2026-06-12 0830.m4a"] = new("audio-0830"),
        });
        var sut = Target(fs);

        var plan = sut.Plan(Source(), Episode(new TimeOnly(8, 30)), Tags());

        Assert.True(plan.AlreadyPresent);
        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12 0830.m4a", plan.Path);
    }

    [Fact]
    public void Plan_TimelessSource_SameContentAsExistingNumberedFile_IsAlreadyPresent()
    {
        // 時刻の無いソース(radikool)の保険サフィックスは ` 2`。こちらも比較対象にする。
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [SourcePath] = new MockFileData("audio-mine"),
            ["/out/J-WAVE/LOGISTEED RADIONOMICS/LOGISTEED RADIONOMICS 2026-06-12.m4a"] = new("audio-other"),
            ["/out/J-WAVE/LOGISTEED RADIONOMICS/LOGISTEED RADIONOMICS 2026-06-12 2.m4a"] = new("audio-mine"),
        });
        var sut = Target(fs);

        var plan = sut.Plan(Source(), Episode(), Tags());

        Assert.True(plan.AlreadyPresent);
        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12 2.m4a", plan.Path);
    }

    [Fact]
    public async Task WriteAsync_WhenComparisonFails_TreatsAsDifferent_AndWrites()
    {
        // 判定できないときは「違う内容」扱いで書く(欠落 > 重複。ADR-0015)。
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [SourcePath] = new MockFileData("audio-bytes"),
            ["/out/J-WAVE/LOGISTEED RADIONOMICS/LOGISTEED RADIONOMICS 2026-06-12.m4a"] = new("audio-bytes"),
        });
        var sut = Target(fs, comparer: new FakeContentComparer(fs) { Fails = true });

        var written = await WriteAsync(sut, Episode(new TimeOnly(8, 30)));

        Assert.EndsWith("LOGISTEED RADIONOMICS 2026-06-12 0830.m4a", written);
        Assert.True(fs.File.Exists(written));
    }
}
