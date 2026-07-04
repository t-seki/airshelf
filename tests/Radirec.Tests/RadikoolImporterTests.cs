using Radirec.Core.Model;
using Radirec.Core.Output;
using Radirec.Core.Sources;

namespace Radirec.Tests;

public sealed class RadikoolImporterTests
{
    // 局=genre, 出演者=artist を返すだけの fake(radikool は識別にタグを読む)。
    private sealed class FakeTagReader(string? genre, string? artist = null) : ITagReader
    {
        public string? ReadArtist(string filePath) => artist;
        public RawTags ReadTags(string filePath) => new(genre, artist);
    }

    // タグ読取が失敗する fake(破損/ロック MP3 を模す)。
    private sealed class ThrowingTagReader : ITagReader
    {
        public string? ReadArtist(string filePath) => null;
        public RawTags ReadTags(string filePath) => throw new IOException("壊れたMP3");
    }

    // radikool のディスク構造は {局コード}/{ファイル}.mp3 の 1 階層なので、局コードは
    // FileSystemRecordingSource 上は ProgramFolder(直上フォルダ)に入る。StationFolder は
    // ルート名(局とは無関係)になる。
    private static RecordingFile Rec(
        string name, string programFolder = "LFR", string stationFolder = "records")
        => new()
        {
            FilePath = "/in/" + name,
            FileName = name,
            StationFolder = stationFolder,
            ProgramFolder = programFolder,
            Extension = Path.GetExtension(name).ToLowerInvariant(),
            SizeBytes = 1024,
            LastWriteTimeUtc = DateTimeOffset.UnixEpoch,
        };

    private static RadikoolImporter Importer(string? genre = "ニッポン放送", string? artist = "出演者")
        => new(new FakeTagReader(genre, artist));

    [Fact]
    public void CanHandle_Mp3WithDateSuffix_True()
    {
        Assert.True(Importer().CanHandle(Rec("オードリーのオールナイトニッポン_20200823.mp3")));
    }

    [Fact]
    public void CanHandle_M4a_False()
    {
        // .m4a は らじれこ側 importer が扱う。
        Assert.False(Importer().CanHandle(Rec("番組_20200823.m4a")));
    }

    [Fact]
    public void CanHandle_Mp3WithoutDateSuffix_False()
    {
        Assert.False(Importer().CanHandle(Rec("なにかの音源.mp3")));
    }

    [Fact]
    public void Parse_Standard_ExtractsProgramDateStationArtist()
    {
        var parsed = Importer(genre: "ニッポン放送", artist: "オードリー")
            .Parse(Rec("オードリーのオールナイトニッポン_20200823.mp3"));

        Assert.Equal("オードリーのオールナイトニッポン", parsed.RawProgram);
        Assert.Equal(new DateOnly(2020, 8, 23), parsed.BroadcastDate);
        Assert.Null(parsed.BroadcastTime);            // radikool は時刻なし
        Assert.Equal("ニッポン放送", parsed.RawStation); // 局は genre 由来
        Assert.False(parsed.StationFromFolder);
        Assert.Equal("オードリー", parsed.Artist);
        Assert.Null(parsed.PartIndex);
    }

    [Fact]
    public void Parse_SameDayDuplicateMarker_BecomesPartIndex()
    {
        // ANN0(ZERO) 2020-02-23 の (2)。番組名から (2) と日付は除かれ、part=2 になる。
        var parsed = Importer()
            .Parse(Rec("日向坂46のオールナイトニッポン0(ZERO)_20200223(2).mp3"));

        Assert.Equal("日向坂46のオールナイトニッポン0(ZERO)", parsed.RawProgram);
        Assert.Equal(new DateOnly(2020, 2, 23), parsed.BroadcastDate);
        Assert.Equal(2, parsed.PartIndex);
    }

    [Fact]
    public void Parse_BareSibling_HasNoPartIndex()
    {
        // (N) 無しの bare は part なし(= 0/単一扱い)。番組名に含まれる (ZERO) は保持する。
        var parsed = Importer()
            .Parse(Rec("日向坂46のオールナイトニッポン0(ZERO)_20200223.mp3"));

        Assert.Equal("日向坂46のオールナイトニッポン0(ZERO)", parsed.RawProgram);
        Assert.Null(parsed.PartIndex);
    }

    [Fact]
    public void Parse_GenreMissing_FallsBackToStationCodeFolder()
    {
        // genre 欠落 → 局コードは直上フォルダ(ProgramFolder)から採る。祖父(StationFolder=ルート名)は使わない。
        var parsed = Importer(genre: null)
            .Parse(Rec("番組_20200823.mp3", programFolder: "LFR", stationFolder: "records"));

        Assert.Equal("LFR", parsed.RawStation);
        Assert.True(parsed.StationFromFolder);
    }

    [Fact]
    public void Parse_InvalidDate_Throws()
    {
        // 2020-02-30 は存在しない日付。
        Assert.Throws<SourceParseException>(
            () => Importer().Parse(Rec("番組_20200230.mp3")));
    }

    [Fact]
    public void Parse_TagReadFailure_ThrowsSourceParseException()
    {
        // 破損/ロックで genre タグを読めない場合は SourceParseException(= 当該ファイルのみ Failed。
        // バッチ/ dry-run 全体は落とさない)。
        var importer = new RadikoolImporter(new ThrowingTagReader());
        Assert.Throws<SourceParseException>(
            () => importer.Parse(Rec("番組_20200823.mp3")));
    }
}
