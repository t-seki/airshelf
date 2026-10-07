using Airshelf.Core.Configuration;
using Airshelf.Hosting;

namespace Airshelf.Tests;

/// <summary>
/// 初回起動時に <c>ConfigSeeder</c> が %LOCALAPPDATA%\RadirecData\ へ生成する sample 設定が
/// ConfigLoader で解析できることを保証する。sample は埋め込みリソースで「初回生成の種」になるため、
/// 壊れると新規インストールが起動時に失敗する。
/// </summary>
public class SampleConfigTests
{
    private static string SamplePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", name);

    [Fact]
    public void Radirecサンプルが解析できパスは相対で入る()
    {
        var config = ConfigLoader.LoadRequired<AirshelfConfig>(SamplePath("radirec.sample.json"));

        // 相対パス(ADR-0006。config ディレクトリ基準で解決される前提の裸名)。
        Assert.Equal("ledger.db", config.LedgerPath);
        Assert.Equal("stations.json", config.StationsFile);
        Assert.Equal("programs.json", config.ProgramsFile);
    }

    [Fact]
    public void 放送局マスタサンプルが解析できる()
    {
        var stations = ConfigLoader.LoadRequired<StationMasterConfig>(SamplePath("stations.sample.json"));
        Assert.NotNull(stations);
    }

    [Fact]
    public void 番組aliasサンプルが解析できる()
    {
        var programs = ConfigLoader.LoadRequired<ProgramMasterConfig>(SamplePath("programs.sample.json"));
        Assert.NotNull(programs);
        // ファイル名の番組名の判断(ADR-0021)の書き方の例(完全一致と前方一致。#240)。取り込みの解決器が受け付ける形であること。
        Assert.All(programs.FilenamePrograms, d => Assert.Equal(FilenameProgramTreatment.SameProgram, d.Treat));
        Assert.Equal(
            [FilenameProgramMatchMode.Exact, FilenameProgramMatchMode.Prefix],
            programs.FilenamePrograms.Select(d => d.Match.Mode));
        _ = new Airshelf.Core.Normalization.ProgramResolver(programs);
    }
}
