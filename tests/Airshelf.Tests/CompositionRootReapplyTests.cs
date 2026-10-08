using Airshelf.Hosting;

namespace Airshelf.Tests;

/// <summary>
/// 再適用の組み立てが、渡された番組・放送局の設定だけを使うこと(トレイ #126 が確認と実行で同じ設定を使うため)。
/// </summary>
public sealed class CompositionRootReapplyTests : IDisposable
{
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "airshelf-test-" + Guid.NewGuid().ToString("N"));

    public CompositionRootReapplyTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        if (Directory.Exists(_work))
            Directory.Delete(_work, recursive: true);
    }

    private AirshelfConfig Config() => new()
    {
        RadioRoot = Path.Combine(_work, "radio"),
        OutputRoot = Path.Combine(_work, "out"),
        LedgerPath = Path.Combine(_work, "ledger.db"), // 無い → dry-run は空の台帳
        ProgramsFile = Path.Combine(_work, "programs.json"),
        StationsFile = Path.Combine(_work, "stations.json"),
    };

    [Fact]
    public async Task Build_with_loaded_settings_does_not_reread_programs_json()
    {
        var config = Config();
        File.WriteAllText(config.ProgramsFile, """{ "Programs": [] }""");
        var settings = CompositionRoot.LoadReapplySettings(config);

        // 確認の後に programs.json が壊れた(編集途中など)。読み直すなら失敗するはず。
        File.WriteAllText(config.ProgramsFile, "{ broken");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => CompositionRoot.LoadReapplySettings(config));

        var (service, disposable) = CompositionRoot.BuildReapply(config, dryRun: true, settings);
        using (disposable)
        {
            var report = await service.RunAsync(dryRun: true, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(report.Items);
        }
    }
}
