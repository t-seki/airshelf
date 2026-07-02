using Radirec.Core.Configuration;
using Radirec.Core.Model;
using Radirec.Core.Normalization;

namespace Radirec.Tests;

public sealed class StationResolverTests
{
    private static StationResolver Build() => new(new StationMasterConfig
    {
        Stations =
        [
            new StationAlias { Canonical = "ニッポン放送", Aliases = ["LFR", "ニッポン放送"] },
            new StationAlias { Canonical = "TOKYO FM", Aliases = ["FMT", "fm_tokyo", "TOKYOFM"] },
        ],
    });

    [Theory]
    [InlineData("LFR", "ニッポン放送")]
    [InlineData("ニッポン放送", "ニッポン放送")]
    [InlineData("FMT", "TOKYO FM")]
    [InlineData("fm_tokyo", "TOKYO FM")]   // radikool 内の表記割れを統合
    [InlineData("TOKYOFM", "TOKYO FM")]    // らじれこの空白なし英語表記
    public void Resolve_KnownAlias_ReturnsCanonical(string raw, string expected)
    {
        var r = Build().Resolve(raw);

        Assert.Equal(expected, r.Canonical);
        Assert.False(r.UsedFallback);
    }

    [Fact]
    public void Resolve_Unknown_FallsBackToRaw()
    {
        var r = Build().Resolve("謎ラジオ");

        Assert.Equal("謎ラジオ", r.Canonical);
        Assert.True(r.UsedFallback);
    }
}

public sealed class ProgramResolverTests
{
    private static ProgramResolver Build() => new(new ProgramMasterConfig
    {
        Programs =
        [
            new ProgramAlias
            {
                // 表記ゆれは空白除去でフォルダが1つに畳まれる(入力側も ToMatchKey で正規化される)。
                Match = new ProgramMatch
                {
                    Station = "J-WAVE",
                    Folders = ["SONYSONPOQUESTFORTHEFUTURE"],
                },
                Canonical = new ProgramCanonical { Album = "SONY SONPO QUEST FOR THE FUTURE" },
            },
        ],
    });

    [Theory]
    [InlineData("SONYSONPO QUEST FOR THE FUTURE")]   // 空白揺れ(旧表記)
    [InlineData("SONY SONPO QUEST FOR THE FUTURE")]  // 新表記
    public void Resolve_KnownProgram_MergesSpellingVariants(string raw)
    {
        var r = Build().Resolve("J-WAVE", raw);

        Assert.Equal("SONY SONPO QUEST FOR THE FUTURE", r.Canonical);
        Assert.False(r.UsedFallback);
    }

    [Fact]
    public void Resolve_UnknownProgram_FallsBackToRaw()
    {
        var r = Build().Resolve("J-WAVE", "新番組");

        Assert.Equal("新番組", r.Canonical);
        Assert.True(r.UsedFallback);
    }

    [Fact]
    public void Resolve_KnownProgramButWrongStation_FallsBack()
    {
        var r = Build().Resolve("TBSラジオ", "SONY SONPO QUEST FOR THE FUTURE");

        Assert.True(r.UsedFallback);
    }

    [Fact]
    public void SingleFolder_HasNoPartIndex()
    {
        Assert.Null(Build().Resolve("J-WAVE", "SONYSONPOQUESTFORTHEFUTURE").PartIndex);
    }

    [Fact]
    public void MultiFolder_AssignsPartIndexByOrder()
    {
        var resolver = new ProgramResolver(new ProgramMasterConfig
        {
            Programs =
            [
                new ProgramAlias
                {
                    Match = new ProgramMatch { Station = "TBSラジオ", Folders = ["ふらっと(1)", "ふらっと(2)"] },
                    Canonical = new ProgramCanonical { Album = "ふらっと" },
                },
            ],
        });

        Assert.Equal(1, resolver.Resolve("TBSラジオ", "ふらっと(1)").PartIndex);
        Assert.Equal(2, resolver.Resolve("TBSラジオ", "ふらっと(2)").PartIndex);
        Assert.Equal("ふらっと", resolver.Resolve("TBSラジオ", "ふらっと(2)").Canonical);
    }
}

public sealed class EpisodeAssemblerTests
{
    private static EpisodeAssembler Build()
    {
        var stations = new StationResolver(new StationMasterConfig
        {
            Stations = [new StationAlias { Canonical = "ニッポン放送", Aliases = ["LFR"] }],
        });
        var programs = new ProgramResolver(new ProgramMasterConfig());
        return new EpisodeAssembler(stations, programs);
    }

    [Fact]
    public void Assemble_ResolvesStationAndBuildsIdentity()
    {
        var source = new ParsedSource
        {
            RawStation = "LFR",
            RawProgram = "オードリーのオールナイトニッポン",
            BroadcastDate = new DateOnly(2024, 10, 6),
            BroadcastTime = new TimeOnly(1, 0, 0),
            RecordingKind = "TimeFree",
        };

        var episode = Build().Assemble(source, artist: "オードリー");

        Assert.Equal("ニッポン放送", episode.Station);               // LFR を解決
        Assert.Equal("オードリーのオールナイトニッポン", episode.Program); // 番組は生名(fallback)
        Assert.Equal(new DateOnly(2024, 10, 6), episode.BroadcastDate);
        Assert.Equal(new TimeOnly(1, 0, 0), episode.BroadcastTime);
        Assert.Equal("オードリー", episode.Artist);
        Assert.Equal("TimeFree", episode.RecordingKind);
        Assert.True(episode.UsedFallback);   // 番組が未登録なので true
    }

    [Fact]
    public void Assemble_NoFallback_WhenBothResolved()
    {
        var stations = new StationResolver(new StationMasterConfig
        {
            Stations = [new StationAlias { Canonical = "ニッポン放送", Aliases = ["LFR"] }],
        });
        var programs = new ProgramResolver(new ProgramMasterConfig
        {
            Programs =
            [
                new ProgramAlias
                {
                    Match = new ProgramMatch { Station = "ニッポン放送", Folders = ["ANN"] },
                    Canonical = new ProgramCanonical { Album = "オールナイトニッポン" },
                },
            ],
        });
        var sut = new EpisodeAssembler(stations, programs);

        var source = new ParsedSource
        {
            RawStation = "LFR",
            RawProgram = "ANN",
            BroadcastDate = new DateOnly(2024, 10, 6),
        };

        var episode = sut.Assemble(source);

        Assert.Equal("ニッポン放送", episode.Station);
        Assert.Equal("オールナイトニッポン", episode.Program);
        Assert.False(episode.UsedFallback);
    }
}
