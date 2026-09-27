using System.Text.Json;
using Radirec.Cli.Core;
using Radirec.Cli.Core.Commands;
using Radirec.Cli.Core.Output;
using Radirec.Core.Attention;
using Radirec.Core.Ledger;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;
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

    // 3 種とも 1 件以上ある状態:兄弟フォルダ Y(1) / Y(2)(設定なし)、理由と時刻のある失敗 1 件、
    // X(1) / X(2) を X へ統合した設定と食い違った回。理由・時刻の無い失敗などは、表を直接出すテスト(Render)で確かめる。
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
    public void 表の注記と失敗の行は管理画面の文言と同じ()
    {
        var at = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var items = new AttentionItems
        {
            UnintegratedSiblings =
            [
                // 組の一部がすでに統合番組 X に入っている(W-124)
                new UnintegratedSiblingGroup(new SiblingFolderGroup("TBSラジオ", "X", ["X(1)", "X(2)", "X(3)"]), ["X(3)"])
                {
                    MergedAlbum = "X",
                },
                // 分けておく組に新しいフォルダが加わった(W-125)
                new UnintegratedSiblingGroup(new SiblingFolderGroup("TBSラジオ", "Y", ["Y(1)", "Y(2)", "Y(3)"]), ["Y(1)", "Y(2)", "Y(3)"])
                {
                    FoldersNewToKeptApart = ["Y(3)"],
                },
            ],
            UnresolvedFailures =
            [
                new IngestFailureRecord(@"I:\radio\TBSラジオ\新しい.m4a", "新しい.m4a を読めません", at),
                new IngestFailureRecord(@"I:\radio\TBSラジオ\古い.m4a", Reason: null, FailedAtUtc: null), // #172 の移行前の記録
            ],
        };

        var output = RenderToString(items);

        Assert.Contains(AttentionText.Count(4), output);
        Assert.Contains(AttentionText.OutsideMerged("X", ["X(3)"]), output);
        Assert.Contains(AttentionText.KeptApartGrown(["Y(3)"]), output);
        Assert.Contains(AttentionText.FailuresHint, output);
        Assert.Contains(AttentionText.FailedAt(at, TimeZoneInfo.Local), output);
        Assert.Contains(AttentionText.NoReason, output);

        // 日時の記録が無い失敗には「失敗:」の行を出さない(管理画面の W-129 と同じ)。
        Assert.Equal(1, output.Split('\n').Count(l => l.TrimStart().StartsWith("失敗:", StringComparison.Ordinal)));
        // ファイル名は Windows の区切りでも切る
        Assert.Contains("  古い.m4a", output);
    }

    [Fact]
    public void 窓より長い取込元のパスも_失敗の塊の中に下げて出す()
    {
        // 空白の無い長いパス。Spectre.Console に任せると、下げた分だけの行を作ってからパスを行頭から折り返していた(#218)。
        const string path = @"C:\Users\Public\radirec-rc4-test\RADIO\TBSラジオ\とても長い番組名のフォルダ\[TBSラジオ]とても長い番組名(TimeFree)_20240923083000.m4a";
        var items = new AttentionItems { UnresolvedFailures = [new IngestFailureRecord(path, "読めません", FailedAt)] };
        const int width = 50;

        var lines = RenderToString(items, width).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var first = lines.FindIndex(l => l.StartsWith("    取込元: ", StringComparison.Ordinal));
        Assert.True(first >= 0, string.Join('\n', lines));
        // 1 行目の後ろから、下げた続きの行が空行を挟まずに並び、つなげると元のパスになる。
        var continued = lines.Skip(first + 1).TakeWhile(l => l.StartsWith("            ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(continued);
        Assert.Equal(path, lines[first]["    取込元: ".Length..] + string.Concat(continued.Select(l => l.TrimStart(' '))));
        Assert.All(lines.Skip(first).Take(continued.Count + 1), l => Assert.True(l.GetCellWidth() <= width, l));
        // 下げた分だけの空行(issue の症状)を作らない。
        Assert.DoesNotContain(lines, l => l.Length > 0 && string.IsNullOrWhiteSpace(l));
    }

    [Fact]
    public void 短い取込元のパスは_ラベルと同じ1行()
    {
        Assert.Equal([@"    取込元: I:\radio\a.m4a"], AttentionCommand.SourceLines(@"I:\radio\a.m4a", 80));
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
    public async Task 台帳のあるフォルダに届かなければ5で_空とは答えない()
    {
        // NAS に届かない UNC と同じく、File.Exists が例外を出さずに false を返す場所(#216)。
        var unreachable = Path.Combine(_dir, "nas-down", "ledger.db");
        var (exitCode, output) = await Run(["attention", "--json"], new StubConfigSource(Config(unreachable)));
        Assert.Equal(ExitCodes.ExternalFailure, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal("attention", json.RootElement.GetProperty("command").GetString());
        Assert.Contains(Path.Combine(_dir, "nas-down"), json.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("result").ValueKind);
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

    private RadirecConfig Config(string? ledgerPath = null) => new()
    {
        LedgerPath = ledgerPath ?? LedgerPath,
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
        var (console, writer) = PlainConsole();
        var exitCode = await RadirecCli.RunAsync(args, "9.9.9-test", console, source);
        return (exitCode, writer.ToString());
    }

    private static string RenderToString(AttentionItems items, int width = 200)
    {
        var (console, writer) = PlainConsole();
        console.Profile.Width = width;
        AttentionCommand.Render(console, items);
        return writer.ToString();
    }

    private static (IAnsiConsole Console, StringWriter Writer) PlainConsole()
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
        return (console, writer);
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
        public void Integrate(SiblingFolderGroup group) { }
        public Task<string?> KeepApartAsync(SiblingFolderGroup group) => Task.FromResult<string?>(null);
        public string? KeepApartBlockedReason => null;
    }
}
