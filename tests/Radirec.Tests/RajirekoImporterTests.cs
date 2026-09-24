using Radirec.Core.Model;
using Radirec.Core.Output;
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
        Assert.Equal("LOGISTEEDRADIONOMICS", r.RawProgram);   // 番組はフォルダ名(ADR-0001)
        Assert.Equal("TimeFree", r.RecordingKind);
        Assert.Equal(new DateOnly(2026, 6, 12), r.BroadcastDate);
        Assert.Equal(new TimeOnly(22, 30, 0), r.BroadcastTime);
        Assert.False(r.StationFromFolder);    }

    // ---- 形状 B: [局]番組_ts(マーカー無し)----

    [Fact]
    public void ClassB_NoMarker_ProgramFromFolder_KindNull()
    {
        var file = Make(
            "[TBSラジオ]アルコ＆ピース D.C_20250528000000.m4a",
            stationFolder: "TBSラジオ", programFolder: "アルコ＆ピースDCGARAGE");

        var r = _sut.Parse(file);

        Assert.Equal("TBSラジオ", r.RawStation);
        Assert.Equal("アルコ＆ピースDCGARAGE", r.RawProgram);   // フォルダ名
        Assert.Null(r.RecordingKind);
        Assert.Equal(new DateOnly(2025, 5, 28), r.BroadcastDate);    }

    // ---- 週替わりゲスト: ファイル名は違ってもフォルダが同じなら同じ番組 ----

    [Fact]
    public void RotatingGuest_DifferentFilenames_SameFolder_YieldSameProgram()
    {
        var folder = "オールナイトニッポン0(ZERO)";
        var a = _sut.Parse(Make(
            "[ニッポン放送]Awesome City Clubのオールナイトニッポン0(ZERO)(TimeFree)_20251207030000.m4a",
            stationFolder: "ニッポン放送", programFolder: folder));
        var b = _sut.Parse(Make(
            "[ニッポン放送]のオールナイトニッポン0(ZERO)(TimeFree)_20241006030000.m4a",
            stationFolder: "ニッポン放送", programFolder: folder));
        var c = _sut.Parse(Make(
            "[ニッポン放送]IMP_20251221030000.m4a",   // マーカー無し・別ゲスト名
            stationFolder: "ニッポン放送", programFolder: folder));

        Assert.Equal(folder, a.RawProgram);
        Assert.Equal(folder, b.RawProgram);
        Assert.Equal(folder, c.RawProgram);   // 3 回とも同じ番組に集約される
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
        Assert.True(r.StationFromFolder);    }

    // ---- 録音種別マーカー: 既知マーカーだけ剥がす(番組名の括弧は種別に誤認しない)----

    [Fact]
    public void TrailingParens_NotAKnownMarker_IsNotTreatedAsKind()
    {
        var file = Make("[X局]番組(特別編)_20240101000000.m4a", programFolder: "番組フォルダ");

        var r = _sut.Parse(file);

        Assert.Null(r.RecordingKind);            // 「(特別編)」は録音種別ではない
        Assert.Equal("番組フォルダ", r.RawProgram); // 番組はフォルダ名
    }

    [Fact]
    public void TrailingKnownMarker_IsExtractedAsKind()
    {
        var file = Make("[X局]番組(特別編)(TimeFree)_20240101000000.m4a", programFolder: "番組フォルダ");

        var r = _sut.Parse(file);

        Assert.Equal("TimeFree", r.RecordingKind);
        Assert.Equal("番組フォルダ", r.RawProgram);
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
    [InlineData("番組_20260612223000.m4a", true)]                    // 形状 C(裸)m4a も受理
    [InlineData("[文化放送]番組(TimeFree)_20221112000000.mp3", true)] // らじれこ命名の .mp3(14桁・[局]付き)を受理(#84)
    [InlineData("番組(TimeFree).m4a", false)]              // タイムスタンプ無し
    [InlineData("[局]番組_20200823.mp3", false)]           // radikool 命名(8桁)は掴まない → RadikoolImporter へ
    [InlineData("番組_20221112000000.mp3", false)]         // [局]無しの裸14桁mp3は保険で対象外(radikool 変換の疑い)
    [InlineData("[局番組(TimeFree)_20231005000000.mp3", false)] // 開き括弧のみ(閉じ ']' 無し)は対象外(Failed にしない)
    // 末尾 (N) マーカーは対象外。統合番組のパートと同じ PartIndex を取り合い、別の録音が黙って欠落しうるため
    // #85 で撤回した。#86 で取り込まないと決めた。
    [InlineData("[TBSラジオ]番組(TimeFree)_20231005000000(2).mp3", false)]
    public void CanHandle_DiscriminatesByExtensionAndTimestamp(string fileName, bool expected)
    {
        var file = Make(fileName);

        Assert.Equal(expected, _sut.CanHandle(file));
    }

    [Fact]
    public void Mp3_WithRajirekoNaming_ParsesLikeM4a()
    {
        // らじれこ命名の .mp3 も M4A と同じくファイル名/フォルダから抽出できる(#84)。
        var file = Make(
            "[文化放送]ますだおかだ…おかしば 11時～12時(TimeFree)_20221112000000.mp3",
            stationFolder: "文化放送", programFolder: "ますだおかだ…おかしば11時1");

        var r = _sut.Parse(file);

        Assert.Equal("文化放送", r.RawStation);
        Assert.Equal("ますだおかだ…おかしば11時1", r.RawProgram); // 番組はフォルダ名(ADR-0001)
        Assert.Equal("TimeFree", r.RecordingKind);
        Assert.Equal(new DateOnly(2022, 11, 12), r.BroadcastDate);
        Assert.Equal(new TimeOnly(0, 0, 0), r.BroadcastTime);
    }

    // タグ(genre=局)を返すだけの fake(radikool の識別に使う)。
    private sealed class FakeTagReader(string? genre) : ITagReader
    {
        public string? ReadArtist(string filePath) => null;
        public RawTags ReadTags(string filePath) => new(genre, null);
    }

    [Fact]
    public void SourceRouter_RoutesMp3ByTimestampDigits()
    {
        // .mp3 は 14桁=らじれこ / 8桁=radikool で自動判別され、正しい importer に回る(#84)。
        var router = new SourceRouter([new RajirekoImporter(), new RadikoolImporter(new FakeTagReader("文化放送"))]);

        var rajirekoMp3 = Make(
            "[文化放送]おかしば(TimeFree)_20221112000000.mp3",
            stationFolder: "文化放送", programFolder: "おかしば11時1");
        var r = router.Parse(rajirekoMp3);
        Assert.Equal("おかしば11時1", r.RawProgram);            // フォルダ名 = らじれこ semantics
        Assert.Equal(new TimeOnly(0, 0, 0), r.BroadcastTime);   // 14桁の時刻を持つ

        var radikoolMp3 = Make("オードリー_20200823.mp3", stationFolder: "LFR", programFolder: "LFR");
        var k = router.Parse(radikoolMp3);
        Assert.Equal("文化放送", k.RawStation);                 // genre タグ由来 = radikool semantics
        Assert.Equal("オードリー", k.RawProgram);                // ファイル名 stem
        Assert.Null(k.BroadcastTime);                           // radikool は時刻なし

        // [局]無しの裸14桁 mp3 はどちらも扱わない(radikool の 8→14桁変換等を Rajireko で誤取込しない保険)。
        var bareMp3 = Make("オードリー_20200823000000.mp3", stationFolder: "LFR", programFolder: "LFR");
        Assert.False(router.CanHandle(bareMp3));
    }
}
