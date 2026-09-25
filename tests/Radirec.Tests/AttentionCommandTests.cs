using System.Text.Json;
using Radirec.Cli.Core;
using Radirec.Cli.Core.Commands;
using Radirec.Cli.Core.Output;
using Radirec.Core.Attention;
using Radirec.Core.Ledger;
using Radirec.Gui.Core.Management;
using Radirec.Hosting;
using Spectre.Console;

namespace Radirec.Tests;

/// <summary>
/// <c>radirec attention</c>(#183)。管理画面のホームの要対応と同じ計算(<see cref="AttentionLoader"/>)を呼ぶこと。
/// 台帳は実物の SQLite、設定は一時フォルダのファイル。
/// </summary>
public sealed class AttentionCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("radirec-attention-cli-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string LedgerPath => Path.Combine(_dir, "ledger.db");
    private string ProgramsPath => Path.Combine(_dir, "programs.json");
    private string StationsPath => Path.Combine(_dir, "stations.json");

    private static readonly DateTimeOffset FailedAt = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    // 3 種とも 1 件以上ある状態:兄弟フォルダ Y(1) / Y(2)(設定なし)、失敗 2 件(時刻・理由の無い古い記録を含む)、
    // X(1) / X(2) を X へ統合した設定と食い違った回。
    private void AllThreeKinds()
    {
        Ingested("X(1)", "X(2)", "Y(1)", "Y(2)");
        using (var ledger = new SqliteLedger(LedgerPath))
        {
            ledger.Record(Path.Combine(_dir, "radio", "bad.m4a"), new FileFingerprint(1, 2), "bad.m4a を読めません", FailedAt);
        }
        File.WriteAllText(ProgramsPath, """
            { "Programs": [ { "Match": { "Station": "TBSラジオ", "Folders": [ "X(1)", "X(2)" ] },
                              "Canonical": { "Album": "X" } } ] }
            """);
    }

    [Fact]
    public async Task jsonは管理画面のホームと同じ計算の結果を出す()
    {
        AllThreeKinds();

        var (exitCode, output) = await Run(["attention", "--json"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        // 管理画面(ManagementWindowHost)が呼ぶのと同じ AttentionLoader の結果を、同じ JSON の形にしたものと一致する。
        var expected = AttentionLoader.Load(LedgerPath, StationsPath, ProgramsPath, ["TimeFree"]);
        Assert.Equal(
            JsonSerializer.Serialize(CliJson.Result("attention", ExitCodes.Success, AttentionJson.From(expected)), CliJson.Options),
            output.TrimEnd());

        using var json = JsonDocument.Parse(output);
        var result = json.RootElement.GetProperty("result");
        Assert.Equal(1 + 1 + 2, result.GetProperty("count").GetInt32()); // 兄弟 1 組 + 失敗 1 件 + 食い違い 2 まとまり

        var sibling = Assert.Single(result.GetProperty("unintegratedSiblings").EnumerateArray());
        Assert.Equal("TBSラジオ", sibling.GetProperty("station").GetString());
        Assert.Equal(["Y(1)", "Y(2)"], sibling.GetProperty("folders").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(["Y(1)", "Y(2)"], sibling.GetProperty("foldersWithoutSetting").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(JsonValueKind.Null, sibling.GetProperty("mergedAlbum").ValueKind);
        Assert.Equal(0, sibling.GetProperty("foldersNewToKeptApart").GetArrayLength());

        var failure = Assert.Single(result.GetProperty("unresolvedFailures").EnumerateArray());
        Assert.EndsWith("bad.m4a", failure.GetProperty("sourcePath").GetString());
        Assert.Equal("bad.m4a を読めません", failure.GetProperty("reason").GetString());
        Assert.Equal(FailedAt, failure.GetProperty("failedAt").GetDateTimeOffset());

        var drift = result.GetProperty("settingsDrift").EnumerateArray().ToList();
        Assert.Equal(["X(1)", "X(2)"], drift.Select(d => d.GetProperty("fromAlbum").GetString()));
        Assert.All(drift, d => Assert.Equal("X", d.GetProperty("toAlbum").GetString()));
        Assert.All(drift, d => Assert.Equal(1, d.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task 件数は管理画面のホームと同じ数え方()
    {
        AllThreeKinds();
        var items = AttentionLoader.Load(LedgerPath, StationsPath, ProgramsPath, ["TimeFree"]);

        var home = new HomeViewModel(
            control: null, degradedReason: null, loadHistory: null, logWarn: _ => { }, showAll: () => { },
            attention: new FixedAttention(items));
        await home.RefreshAttentionAsync();

        Assert.Equal(4, home.AttentionCount);
        Assert.Equal(home.AttentionCount, AttentionJson.From(items).Count);
    }

    [Fact]
    public void 設定から管理画面と同じ台帳と設定ファイルを読む()
    {
        AllThreeKinds();

        var fromCli = AttentionCommand.Load(Config());
        var fromLoader = AttentionLoader.Load(LedgerPath, StationsPath, ProgramsPath, ["TimeFree"]);

        Assert.Equal(fromLoader.UnintegratedSiblings.Select(s => s.Group.Folders), fromCli.UnintegratedSiblings.Select(s => s.Group.Folders));
        Assert.Equal(fromLoader.UnresolvedFailures, fromCli.UnresolvedFailures);
        Assert.Equal(fromLoader.SettingsDrift, fromCli.SettingsDrift);
    }

    [Fact]
    public async Task 表は管理画面のホームと同じ見出しと行で出す()
    {
        AllThreeKinds();
        var items = AttentionLoader.Load(LedgerPath, StationsPath, ProgramsPath, ["TimeFree"]);

        var (exitCode, output) = await Run(["attention"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains(AttentionText.Count(4), output);
        Assert.Contains(AttentionText.SiblingsHeading, output);
        Assert.Contains("TBSラジオ: Y(1)(設定なし) / Y(2)(設定なし)", output);
        Assert.Contains(AttentionText.FailuresHeading, output);
        Assert.Contains("bad.m4a を読めません", output);
        Assert.Contains(AttentionText.DriftHeading, output);
        foreach (var drift in items.SettingsDrift)
            Assert.Contains(AttentionText.DriftLine(drift), output);
    }

    [Fact]
    public void 再適用が要る番組の行は管理画面と同じ表記()
    {
        var renamed = new SettingsDriftGroup("TBSラジオ", "X(1)", "X", 3);
        var partOnly = new SettingsDriftGroup("TBSラジオ", "X", "X", 2);

        Assert.Equal(AttentionText.DriftLine(renamed), AttentionCommand.DriftLine(renamed));
        Assert.Equal(AttentionText.DriftLine(partOnly), AttentionCommand.DriftLine(partOnly));
    }

    [Fact]
    public async Task 何も無ければ空で0_台帳も作らない()
    {
        var (exitCode, output) = await Run(["attention", "--json"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(output);
        var result = json.RootElement.GetProperty("result");
        Assert.Equal(0, result.GetProperty("count").GetInt32());
        Assert.Equal(0, result.GetProperty("unintegratedSiblings").GetArrayLength());
        Assert.Equal(0, result.GetProperty("unresolvedFailures").GetArrayLength());
        Assert.Equal(0, result.GetProperty("settingsDrift").GetArrayLength());
        Assert.False(File.Exists(LedgerPath));

        var (tableExit, table) = await Run(["attention"]);
        Assert.Equal(ExitCodes.Success, tableExit);
        Assert.Contains(AttentionText.None, table);
    }

    [Fact]
    public async Task programsjsonを読めなければ1()
    {
        File.WriteAllText(ProgramsPath, "{ \"Programs\": [ ");

        var (exitCode, output) = await Run(["attention", "--json"]);

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal("attention", json.RootElement.GetProperty("command").GetString());
        Assert.StartsWith("要対応を出せませんでした", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task 設定を読めなければ1()
    {
        var (exitCode, _) = await Run(["attention"], new FailingConfigSource());

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
    }

    [Fact]
    public void コマンド名が決まる()
    {
        Assert.Equal("attention", RadirecCli.CommandOf(["attention", "--json"]));
    }

    // ---- 部品 ----

    private RadirecConfig Config() => new()
    {
        LedgerPath = LedgerPath,
        StationsFile = StationsPath,
        ProgramsFile = ProgramsPath,
        KnownRecordingKinds = ["TimeFree"],
    };

    private void Ingested(params string[] folders)
    {
        using var ledger = new SqliteLedger(LedgerPath);
        foreach (var folder in folders)
        {
            ledger.Record(new LedgerEntry
            {
                IdentityKey = $"TBSラジオ|{folder}",
                Station = "TBSラジオ",
                Program = folder,
                BroadcastDate = new DateOnly(2024, 9, 23),
                SourcePath = Path.Combine(_dir, "radio", "TBSラジオ", folder, $"[TBSラジオ]{folder}(TimeFree)_20240923083000.m4a"),
                OutputPath = Path.Combine(_dir, "out", folder + ".m4a"),
                RuleVersion = "1",
                UsedFallback = true,
                ImportedAtUtc = DateTimeOffset.UnixEpoch,
            });
        }
    }

    private Task<(int ExitCode, string Output)> Run(string[] args) => Run(args, new StubConfigSource(Config()));

    private static async Task<(int ExitCode, string Output)> Run(string[] args, ICliConfigSource source)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200; // 行を折り返さない
        var exitCode = await RadirecCli.RunAsync(args, "9.9.9-test", console, source);
        return (exitCode, writer.ToString());
    }

    private sealed class StubConfigSource(RadirecConfig config) : ICliConfigSource
    {
        public bool TryLoad(out RadirecConfig? loaded, out string? error)
        {
            loaded = config;
            error = null;
            return true;
        }
    }

    private sealed class FailingConfigSource : ICliConfigSource
    {
        public bool TryLoad(out RadirecConfig? config, out string? error)
        {
            config = null;
            error = "radirec.json を読めません";
            return false;
        }
    }

    private sealed class FixedAttention(AttentionItems items) : IAttentionActions
    {
        public AttentionItems Compute() => items;
        public void Integrate(Radirec.Core.Normalization.SiblingFolderGroup group) { }
        public Task<string?> KeepApartAsync(Radirec.Core.Normalization.SiblingFolderGroup group) => Task.FromResult<string?>(null);
        public string? KeepApartBlockedReason => null;
    }
}
