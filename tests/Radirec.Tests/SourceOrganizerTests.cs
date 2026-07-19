using System.IO.Abstractions.TestingHelpers;
using Radirec.Core.Configuration;
using Radirec.Core.Ingestion;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Organization;
using Radirec.Core.Sources;

namespace Radirec.Tests;

public sealed class SourceOrganizerTests
{
    private const string From = "/src";
    private const string To = "/dst";

    // ニッポン放送(alias LFR)と NHK-FM(alias NHK-FM(東京))。alias 分裂の再利用検証に使う。
    private static StationMasterConfig Stations() => new()
    {
        Stations =
        [
            new StationAlias { Canonical = "ニッポン放送", Aliases = ["LFR", "ニッポン放送"] },
            new StationAlias { Canonical = "NHK-FM", Aliases = ["NHK-FM（東京）", "NHK-FM"] },
        ],
    };

    private static SourceOrganizer Build(
        MockFileSystem fs, ISourceImporter importer,
        IEnumerable<string>? skip = null, IStabilityChecker? stability = null)
        => new(
            fs,
            new FileSystemRecordingSource(fs, From),
            importer,
            new StationResolver(Stations()),
            stability ?? new AlwaysStable(),
            new RecordingSkipPolicy(skip ?? []),
            To);

    private sealed class FakeImporter : ISourceImporter
    {
        public Func<RecordingFile, bool> CanHandleFn { get; init; } = _ => true;
        public Func<RecordingFile, ParsedSource> ParseFn { get; init; } =
            _ => throw new SourceParseException("no parse configured");

        public bool CanHandle(RecordingFile file) => CanHandleFn(file);
        public ParsedSource Parse(RecordingFile file) => ParseFn(file);
    }

    private sealed class AlwaysStable : IStabilityChecker
    {
        public bool IsStable(RecordingFile file) => true;
    }

    private sealed class NeverStable : IStabilityChecker
    {
        public bool IsStable(RecordingFile file) => false;
    }

    [Fact]
    public void Rajireko_MovedTo_CanonicalStation_RawProgram()
    {
        var fs = new MockFileSystem();
        var name = "[ニッポン放送]オードリーのオールナイトニッポン(TimeFree)_20240114010000.m4a";
        // 余分な d_radio 階層があっても親=番組・祖父=局で解決される。
        var src = $"/src/d_radio/ニッポン放送/オードリーのオールナイトニッポン/{name}";
        fs.AddFile(src, new MockFileData("audio"));

        var item = Assert.Single(Build(fs, new RajirekoImporter()).Run(dryRun: false, copy: false).Items);

        Assert.Equal(OrganizeOutcome.Moved, item.Outcome);
        Assert.Equal($"/dst/ニッポン放送/オードリーのオールナイトニッポン/{name}", item.TargetPath);
        Assert.True(fs.File.Exists(item.TargetPath!));
        Assert.False(fs.File.Exists(src)); // 移動した
    }

    [Fact]
    public void Rajireko_KeepsRawProgramFolder_PreservingPartSplit()
    {
        // 統合番組の (1) フォルダは生のまま維持(canonical album に畳むと再取込のパート -N が壊れる)。
        var fs = new MockFileSystem();
        var src = "/src/TBSラジオ/パンサー向井のふらっと(1)/[TBSラジオ]パンサー向井のふらっと(TimeFree)_20241223083000.m4a";
        fs.AddFile(src, new MockFileData("a"));

        var item = Assert.Single(Build(fs, new RajirekoImporter()).Run(false, false).Items);

        Assert.Equal(OrganizeOutcome.Moved, item.Outcome);
        // 局は未登録 → fallback で "TBSラジオ"、番組フォルダは (1) 付きのまま。
        Assert.Equal(
            "/dst/TBSラジオ/パンサー向井のふらっと(1)/[TBSラジオ]パンサー向井のふらっと(TimeFree)_20241223083000.m4a",
            item.TargetPath);
    }

    [Fact]
    public void Radikool_FiledUnder_CanonicalStation_NotFolderCode()
    {
        // radikool は局=タグ・番組=ファイル名 stem。局コードフォルダ(LFR)に惑わされず正規局へ振る。
        var fs = new MockFileSystem();
        var src = "/src/LFR/オードリーのオールナイトニッポン_20200823.mp3";
        fs.AddFile(src, new MockFileData("mp3"));
        var importer = new FakeImporter
        {
            CanHandleFn = f => f.Extension == ".mp3",
            ParseFn = _ => new ParsedSource
            {
                RawStation = "ニッポン放送", // genre タグ由来
                RawProgram = "オードリーのオールナイトニッポン", // filename stem
                BroadcastDate = new DateOnly(2020, 8, 23),
            },
        };

        var item = Assert.Single(Build(fs, importer).Run(false, false).Items);

        Assert.Equal(
            "/dst/ニッポン放送/オードリーのオールナイトニッポン/オードリーのオールナイトニッポン_20200823.mp3",
            item.TargetPath);
    }

    [Fact]
    public void ReusesExistingStationFolder_ResolvingToSameCanonical()
    {
        // 移動先に らじれこ の alias フォルダ NHK-FM(東京) が既にあると、canonical NHK-FM ではなく
        // その既存フォルダを再利用してフォルダ分裂を防ぐ。
        var fs = new MockFileSystem();
        fs.AddDirectory("/dst/NHK-FM（東京）");
        var src = "/src/x/NHK-FM/番組/[NHK-FM]番組(TimeFree)_20240101120000.m4a";
        fs.AddFile(src, new MockFileData("a"));

        var item = Assert.Single(Build(fs, new RajirekoImporter()).Run(false, false).Items);

        Assert.Equal(OrganizeOutcome.Moved, item.Outcome);
        Assert.StartsWith("/dst/NHK-FM（東京）/", item.TargetPath);
    }

    [Fact]
    public void DuplicateAtTarget_SameContent_Skipped_SourceRetained()
    {
        var fs = new MockFileSystem();
        var name = "[ニッポン放送]番組(TimeFree)_20240101010000.m4a";
        var src = $"/src/ニッポン放送/番組/{name}";
        var tgt = $"/dst/ニッポン放送/番組/{name}";
        var when = new DateTimeOffset(2024, 1, 1, 1, 0, 0, TimeSpan.Zero);
        fs.AddFile(src, new MockFileData("same") { LastWriteTime = when });
        fs.AddFile(tgt, new MockFileData("same") { LastWriteTime = when });

        var item = Assert.Single(Build(fs, new RajirekoImporter()).Run(false, false).Items);

        Assert.Equal(OrganizeOutcome.DuplicateSkipped, item.Outcome);
        Assert.True(fs.File.Exists(src)); // 上書きも移動もしない。移動元は残る
    }

    [Fact]
    public void ConflictAtTarget_DifferentContent_NotOverwritten_Reported()
    {
        var fs = new MockFileSystem();
        var name = "[ニッポン放送]番組(TimeFree)_20240101010000.m4a";
        var src = $"/src/ニッポン放送/番組/{name}";
        var tgt = $"/dst/ニッポン放送/番組/{name}";
        fs.AddFile(src, new MockFileData("SOURCE-CONTENT"));
        fs.AddFile(tgt, new MockFileData("different"));

        var report = Build(fs, new RajirekoImporter()).Run(false, false);
        var item = Assert.Single(report.Items);

        Assert.Equal(OrganizeOutcome.Conflict, item.Outcome);
        Assert.Equal("different", fs.File.ReadAllText(tgt)); // 別内容を上書きしない
        Assert.True(fs.File.Exists(src));
        Assert.Equal(1, report.NeedsAttention);
    }

    [Fact]
    public void DryRun_ReportsMoved_ButDoesNotTouchDisk()
    {
        var fs = new MockFileSystem();
        var src = "/src/ニッポン放送/番組/[ニッポン放送]番組(TimeFree)_20240101010000.m4a";
        fs.AddFile(src, new MockFileData("a"));

        var item = Assert.Single(Build(fs, new RajirekoImporter()).Run(dryRun: true, copy: false).Items);

        Assert.Equal(OrganizeOutcome.Moved, item.Outcome);
        Assert.True(fs.File.Exists(src));                 // 動かさない
        Assert.False(fs.Directory.Exists("/dst/ニッポン放送")); // ディレクトリも作らない
    }

    [Fact]
    public void Copy_KeepsSource_AndCreatesTarget()
    {
        var fs = new MockFileSystem();
        var src = "/src/ニッポン放送/番組/[ニッポン放送]番組(TimeFree)_20240101010000.m4a";
        fs.AddFile(src, new MockFileData("a"));

        var item = Assert.Single(Build(fs, new RajirekoImporter()).Run(false, copy: true).Items);

        Assert.Equal(OrganizeOutcome.Copied, item.Outcome);
        Assert.True(fs.File.Exists(src));            // 移動元を残す
        Assert.True(fs.File.Exists(item.TargetPath!));
    }

    [Fact]
    public void Junk_MatchingSkipPattern_NotFiled_SourceRetained()
    {
        var fs = new MockFileSystem();
        var src = "/src/x/番組/この時間はＮＨＫネットラジオでは_20241021200956.m4a";
        fs.AddFile(src, new MockFileData("a"));

        var item = Assert.Single(
            Build(fs, new RajirekoImporter(), skip: ["この時間はＮＨＫネットラジオでは"]).Run(false, false).Items);

        Assert.Equal(OrganizeOutcome.SkippedJunk, item.Outcome);
        Assert.True(fs.File.Exists(src)); // 綺麗アーカイブに混ぜず移動元に残す
    }

    [Fact]
    public void Unsupported_WhenNoImporterHandles()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/src/x/番組/weird.m4a", new MockFileData("a"));
        var importer = new FakeImporter { CanHandleFn = _ => false };

        var item = Assert.Single(Build(fs, importer).Run(false, false).Items);

        Assert.Equal(OrganizeOutcome.Unsupported, item.Outcome);
    }

    [Fact]
    public void Unstable_NotMoved_SourceRetained()
    {
        // 書込中/0 バイトの録音は動かさない(move は移動元を消すため欠けた録音を運んでしまう)。
        var fs = new MockFileSystem();
        var src = "/src/ニッポン放送/番組/[ニッポン放送]番組(TimeFree)_20240101010000.m4a";
        fs.AddFile(src, new MockFileData("a"));

        var item = Assert.Single(
            Build(fs, new RajirekoImporter(), stability: new NeverStable()).Run(false, false).Items);

        Assert.Equal(OrganizeOutcome.SkippedUnstable, item.Outcome);
        Assert.True(fs.File.Exists(src));
    }

    [Fact]
    public void StationCodeFolder_NotReused_FilesUnderCanonical()
    {
        // 移動先に radikool の局コードフォルダ LFR が既にあっても、canonical フォルダへ振る
        // (コードは canonical の表記変種でないので reuse しない)。
        var fs = new MockFileSystem();
        fs.AddDirectory("/dst/LFR");
        var src = "/src/x/ニッポン放送/番組/[ニッポン放送]番組(TimeFree)_20240101010000.m4a";
        fs.AddFile(src, new MockFileData("a"));

        var item = Assert.Single(Build(fs, new RajirekoImporter()).Run(false, false).Items);

        Assert.Equal(
            "/dst/ニッポン放送/番組/[ニッポン放送]番組(TimeFree)_20240101010000.m4a", item.TargetPath);
    }

    [Fact]
    public void SameContent_DifferentMtime_IsDuplicate_NotConflict()
    {
        // 内容が同一なら、最終更新時刻がずれていても衝突でなく重複(スキップ)として扱う。
        var fs = new MockFileSystem();
        var name = "[ニッポン放送]番組(TimeFree)_20240101010000.m4a";
        var src = $"/src/ニッポン放送/番組/{name}";
        var tgt = $"/dst/ニッポン放送/番組/{name}";
        fs.AddFile(src, new MockFileData("identical") { LastWriteTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        fs.AddFile(tgt, new MockFileData("identical") { LastWriteTime = new DateTimeOffset(2023, 5, 5, 0, 0, 0, TimeSpan.Zero) });

        var item = Assert.Single(Build(fs, new RajirekoImporter()).Run(false, false).Items);

        Assert.Equal(OrganizeOutcome.DuplicateSkipped, item.Outcome);
    }

    [Fact]
    public void ParseThrowsNonSourceParse_ReportedAsFailed_BatchContinues()
    {
        // radikool の part 序数 int.Parse Overflow 等、SourceParseException 以外でもバッチを止めない。
        var fs = new MockFileSystem();
        fs.AddFile("/src/a/番組/x.m4a", new MockFileData("a"));
        fs.AddFile("/src/b/番組/y.m4a", new MockFileData("b"));
        var importer = new FakeImporter
        {
            CanHandleFn = _ => true,
            ParseFn = f => f.FileName.StartsWith("x")
                ? throw new OverflowException("part overflow")
                : new ParsedSource
                {
                    RawStation = "ニッポン放送",
                    RawProgram = "番組",
                    BroadcastDate = new DateOnly(2024, 1, 1),
                },
        };

        var report = Build(fs, importer).Run(false, false);

        Assert.Equal(2, report.Items.Count);                   // バッチは止まらない
        Assert.Equal(1, report.Count(OrganizeOutcome.Failed)); // x は Failed
        Assert.Equal(1, report.Count(OrganizeOutcome.Moved));  // y は移動
    }

    [Fact]
    public void ParseFailure_ReportedAsFailed_SourceRetained()
    {
        var fs = new MockFileSystem();
        var src = "/src/x/番組/weird.m4a";
        fs.AddFile(src, new MockFileData("a"));
        var importer = new FakeImporter
        {
            CanHandleFn = _ => true,
            ParseFn = _ => throw new SourceParseException("bad name"),
        };

        var report = Build(fs, importer).Run(false, false);
        var item = Assert.Single(report.Items);

        Assert.Equal(OrganizeOutcome.Failed, item.Outcome);
        Assert.True(fs.File.Exists(src));
        Assert.Equal(1, report.NeedsAttention);
    }
}
