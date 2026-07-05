using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// 設定内相対パスの解決基準(ADR-0006)。後方互換の要 = 絶対パスは素通り・相対は基準ディレクトリへ寄せる。
/// </summary>
public class ConfigPathsTests
{
    [Fact]
    public void Rebase_絶対パスは素通りする()
    {
        // 実行 OS で絶対と判定されるパスを使う(Linux CI では C:\ は絶対扱いされないため)。
        // 本番 Windows では C:\radirec\ledger.db が絶対として素通りする挙動と等価。
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
        var configPath = Path.Combine(dir, "radirec.json");
        var absoluteLedger = Path.Combine(dir, "sub", "ledger.db");
        var config = new RadirecConfig
        {
            LedgerPath = absoluteLedger,   // 絶対 → 不変
            StationsFile = "stations.json", // 相対 → 寄せる
            ProgramsFile = "programs.json", // 相対 → 寄せる
        };

        ConfigPaths.RebaseAgainst(config, configPath);

        Assert.Equal(absoluteLedger, config.LedgerPath);
        Assert.Equal(Path.Combine(dir, "stations.json"), config.StationsFile);
        Assert.Equal(Path.Combine(dir, "programs.json"), config.ProgramsFile);
    }

    [Fact]
    public void Resolve_明示パスを優先し省略時は既定を返す()
    {
        Assert.Equal("/explicit/radirec.json", ConfigPaths.Resolve("/explicit/radirec.json"));
        Assert.Equal(ConfigPaths.DefaultConfigPath, ConfigPaths.Resolve(null));
        Assert.Equal(ConfigPaths.DefaultConfigPath, ConfigPaths.Resolve("   "));
    }
}
