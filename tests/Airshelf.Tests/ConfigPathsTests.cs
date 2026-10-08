using Airshelf.Hosting;

namespace Airshelf.Tests;

/// <summary>
/// 設定内相対パスの解決基準(ADR-0006)。後方互換の要 = 絶対パスは素通り・相対は基準ディレクトリへ寄せる。
/// </summary>
public class ConfigPathsTests
{
    [Fact]
    public void Rebase_絶対パスは素通りする()
    {
        // 実行 OS で絶対と判定されるパスを使う(Linux では C:\ は絶対扱いされないため)。
        // 本番 Windows では C:\airshelf\ledger.db が絶対として素通りする挙動と等価。
        var absolute = Path.Combine(Path.GetTempPath(), "ledger.db");

        Assert.Equal(absolute, ConfigPaths.Rebase("/some/base", absolute));
    }

    [Fact]
    public void Rebase_相対パスは基準ディレクトリへ寄せる()
    {
        var baseDir = Path.GetTempPath();

        Assert.Equal(Path.Combine(baseDir, "ledger.db"), ConfigPaths.Rebase(baseDir, "ledger.db"));
    }

    [Fact]
    public void Rebase_空文字は素通りする()
    {
        Assert.Equal(string.Empty, ConfigPaths.Rebase("/base", string.Empty));
    }

    [Fact]
    public void RebaseAgainst_相対だけを設定ファイルのディレクトリへ寄せる()
    {
        var dir = Path.GetTempPath();
        var configPath = Path.Combine(dir, "airshelf.json");
        var absoluteLedger = Path.Combine(dir, "sub", "ledger.db");
        var config = new AirshelfConfig
        {
            RadioRoot = "recordings",         // 相対 → 寄せる
            OutputRoot = absoluteLedger,      // 絶対 → 不変(UNC/ドライブ相当)
            LedgerPath = absoluteLedger,      // 絶対 → 不変
            StationsFile = "stations.json",   // 相対 → 寄せる
            ProgramsFile = "programs.json",   // 相対 → 寄せる
        };

        ConfigPaths.RebaseAgainst(config, configPath);

        Assert.Equal(Path.Combine(dir, "recordings"), config.RadioRoot);
        Assert.Equal(absoluteLedger, config.OutputRoot);
        Assert.Equal(absoluteLedger, config.LedgerPath);
        Assert.Equal(Path.Combine(dir, "stations.json"), config.StationsFile);
        Assert.Equal(Path.Combine(dir, "programs.json"), config.ProgramsFile);
    }

    [Fact]
    public void Resolve_明示パスを優先し省略時は既定を返す()
    {
        Assert.Equal("/explicit/airshelf.json", ConfigPaths.Resolve("/explicit/airshelf.json"));
        Assert.Equal(ConfigPaths.DefaultConfigPath, ConfigPaths.Resolve(null));
        Assert.Equal(ConfigPaths.DefaultConfigPath, ConfigPaths.Resolve("   "));
    }

    /// <summary>
    /// 設定ページの「ファイルを開く」(#143 / #152)が開く場所。実際の読み込み(<see cref="ConfigLoading.TryLoad"/> の rebase)を
    /// 通した設定から求め、相対は airshelf.json のディレクトリ基準・絶対は素通りになることを確かめる。
    /// </summary>
    [Fact]
    public void Locate_読み込んだ設定からairshelf_json基準で場所を求める()
    {
        var dir = Directory.CreateTempSubdirectory("airshelf-locate-").FullName;
        try
        {
            var absoluteStations = Path.Combine(dir, "elsewhere", "my-stations.json");
            var configPath = Path.Combine(dir, "airshelf.json");
            File.WriteAllText(configPath, $$"""
                {
                  "ProgramsFile": "sub/programs.json",
                  "StationsFile": {{System.Text.Json.JsonSerializer.Serialize(absoluteStations)}}
                }
                """);
            Assert.True(ConfigLoading.TryLoad(configPath, out var config, out var error), error);

            Assert.Equal(configPath, ConfigPaths.Locate(ConfigFile.Airshelf, configPath, config!.StationsFile, config.ProgramsFile));
            Assert.Equal(
                Path.Combine(dir, "sub", "programs.json"),
                ConfigPaths.Locate(ConfigFile.Programs, configPath, config.StationsFile, config.ProgramsFile));
            Assert.Equal(
                absoluteStations,
                ConfigPaths.Locate(ConfigFile.Stations, configPath, config.StationsFile, config.ProgramsFile));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Locate_設定を読めていなければairshelf_jsonだけ決まる()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "airshelf.json");

        Assert.Equal(configPath, ConfigPaths.Locate(ConfigFile.Airshelf, configPath, null, null));
        Assert.Null(ConfigPaths.Locate(ConfigFile.Programs, configPath, null, null));
        Assert.Null(ConfigPaths.Locate(ConfigFile.Stations, configPath, null, null));
    }

    [Fact]
    public void Locate_パスが空なら場所は決まらない()
    {
        // 空の ProgramsFile / StationsFile は rebase でも空のまま、LoadOptional も何も読まない(= 使っていない)。
        var configPath = Path.Combine(Path.GetTempPath(), "airshelf.json");

        Assert.Null(ConfigPaths.Locate(ConfigFile.Programs, configPath, "x.json", ""));
        Assert.Null(ConfigPaths.Locate(ConfigFile.Stations, configPath, "  ", "x.json"));
    }
}
