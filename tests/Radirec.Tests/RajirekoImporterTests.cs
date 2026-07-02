using Radirec.Core.Model;
using Radirec.Core.Sources;

namespace Radirec.Tests;

public sealed class RajirekoImporterTests
{
    private readonly RajirekoImporter _sut = new();

    private static RecordingFile Make(
        string fileName, string stationFolder = "局", string programFolder = "番組")
        => new()
        {
            FilePath = $@"I:\radio\tmp\{stationFolder}\{programFolder}\{fileName}",
            FileName = fileName,
            StationFolder = stationFolder,
            ProgramFolder = programFolder,
            Extension = Path.GetExtension(fileName).ToLowerInvariant(),
        };

    // ---- 形状 A: [局]番組(TimeFree)_ts ----

    [Fact]
    public void ClassA_Standard_ParsesAllFieldsFromFilename()
    {
        var file = Make(
            "[J-WAVE]LOGISTEED RADIONOMICS(TimeFree)_20260612223000.m4a",
            stationFolder: "J-WAVE", programFolder: "LOGISTEEDRADIONOMICS");

        var r = _sut.Parse(file);

        Assert.Equal("J-WAVE", r.RawStation);
        Assert.Equal("LOGISTEED RADIONOMICS", r.RawProgram);
        Assert.Equal("TimeFree", r.RecordingKind);
        Assert.Equal(new DateOnly(2026, 6, 12), r.BroadcastDate);
        Assert.Equal(new TimeOnly(22, 30, 0), r.BroadcastTime);
        Assert.False(r.StationFromFolder);
        Assert.False(r.ProgramFromFolder);
    }

    // ---- 形状 B: [局]番組_ts(マーカー無し)----

    [Fact]
    public void ClassB_NoMarker_ProgramFromFilename_KindNull()
    {
        var file = Make(
            "[TBSラジオ]アルコ＆ピース D.C_20250528000000.m4a",
            stationFolder: "TBSラジオ", programFolder: "アルコ＆ピース D.C");

        var r = _sut.Parse(file);

        Assert.Equal("TBSラジオ", r.RawStation);
        Assert.Equal("アルコ＆ピース D.C", r.RawProgram);
        Assert.Null(r.RecordingKind);
        Assert.Equal(new DateOnly(2025, 5, 28), r.BroadcastDate);
        Assert.False(r.ProgramFromFolder);
    }

    // ---- 形状 C: 局プレフィックス無し → フォルダを権威に ----

    [Fact]
    public void ClassC_NoStationPrefix_UsesFolderForStationAndProgram()
    {
        var file = Make(
            "さくらひなたロッチの伸びしろラジオ　１１／１１　第１６０回_20241111200956.m4a",
            stationFolder: "NHKラジオ第1(radiko)",
            programFolder: "さくらひなたロッチの伸びしろラジオ");

        var r = _sut.Parse(file);

        Assert.Equal("NHKラジオ第1(radiko)", r.RawStation);
        Assert.Equal("さくらひなたロッチの伸びしろラジオ", r.RawProgram);
        Assert.Null(r.RecordingKind);
        Assert.Equal(new DateOnly(2024, 11, 11), r.BroadcastDate);
        Assert.Equal(new TimeOnly(20, 9, 56), r.BroadcastTime);
        Assert.True(r.StationFromFolder);
        Assert.True(r.ProgramFromFolder);
    }

    // ---- 番組名が括弧を含むケース(誤って剥がさない)----

    [Fact]
    public void ProgramNameWithParens_NotAKnownMarker_IsKept()
    {
        var file = Make("[X局]番組(特別編)_20240101000000.m4a");

        var r = _sut.Parse(file);

        Assert.Equal("番組(特別編)", r.RawProgram);
        Assert.Null(r.RecordingKind);
    }

    [Fact]
    public void ProgramNameWithParens_PlusTrailingMarker_StripsOnlyMarker()
    {
        var file = Make("[X局]番組(特別編)(TimeFree)_20240101000000.m4a");

        var r = _sut.Parse(file);

        Assert.Equal("番組(特別編)", r.RawProgram);
        Assert.Equal("TimeFree", r.RecordingKind);
    }

    // ---- 日時の境界・不正 ----

    [Fact]
    public void LeapDay_IsValid()
    {
        var file = Make("[X局]番組(TimeFree)_20240229123000.m4a");

        var r = _sut.Parse(file);

        Assert.Equal(new DateOnly(2024, 2, 29), r.BroadcastDate);
    }

    [Theory]
    [InlineData("[X局]番組(TimeFree)_20250229123000.m4a")] // 非閏年の2/29
    [InlineData("[X局]番組(TimeFree)_20261340000000.m4a")] // 13月40日
    public void InvalidDateTime_Throws(string fileName)
    {
        var file = Make(fileName);

        Assert.Throws<SourceParseException>(() => _sut.Parse(file));
    }

    [Fact]
    public void NoTimestampSuffix_Throws()
    {
        var file = Make("[X局]番組(TimeFree).m4a");

        Assert.Throws<SourceParseException>(() => _sut.Parse(file));
    }

    // ---- CanHandle ----

    [Theory]
    [InlineData("[J-WAVE]番組(TimeFree)_20260612223000.m4a", true)]
    [InlineData("番組_20260612223000.m4a", true)]
    [InlineData("番組(TimeFree).m4a", false)]              // タイムスタンプ無し
    [InlineData("[局]番組_20200823.mp3", false)]           // 別拡張子(radikool)
    public void CanHandle_DiscriminatesByExtensionAndTimestamp(string fileName, bool expected)
    {
        var file = Make(fileName);

        Assert.Equal(expected, _sut.CanHandle(file));
    }
}
