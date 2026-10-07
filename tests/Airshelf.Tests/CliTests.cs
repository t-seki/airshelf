using System.Text.Json;
using Airshelf.Cli.Core;
using Airshelf.Cli.Core.Commands;
using Airshelf.Cli.Core.Output;
using Airshelf.Core.Ingestion;
using Airshelf.Core.Jellyfin;
using Airshelf.Core.Ledger;
using Airshelf.Core.Normalization;
using Airshelf.Core.Reapply;
using Airshelf.Hosting;
using Airshelf.Hosting.Control;
using Spectre.Console;

namespace Airshelf.Tests;

/// <summary>
/// radirec.exe の終了コード・JSON の形・解析(#178・ADR-0020)。CI は exe を起動しないので、ここで守る。
/// </summary>
public sealed class CliTests
{
    // ---- 終了コード ----

    [Fact]
    public void 取り込みで失敗が無ければ0()
    {
        Assert.Equal(ExitCodes.Success, ExitCodeRules.For(Ingest(IngestOutcome.Imported)));
    }

    [Fact]
    public void 保留と取り込まない判断は失敗ではないので0()
    {
        // ADR-0021。保留は判断を待っているだけで、録音ファイルにも処理にも誤りは無い。
        var report = Ingest(IngestOutcome.Imported, IngestOutcome.Held, IngestOutcome.SkippedByDecision);

        Assert.Equal(ExitCodes.Success, ExitCodeRules.For(report));
        Assert.Equal(ExitCodes.Success, ExitCodeRules.For(ScanRunReport.From(report)));
    }

    [Fact]
    public void ingestのjsonは保留の件数とファイル名の番組名を出す()
    {
        var report = Ingest(IngestOutcome.Held, IngestOutcome.Held, IngestOutcome.SkippedByDecision);
        report = report with
        {
            Items = report.Items.Select(i => i with { FilenameProgram = "特番" }).ToList(),
        };

        var json = JsonSerializer.SerializeToElement(IngestJson.From(report), CliJson.Options);

        Assert.Equal(2, json.GetProperty("counts").GetProperty("held").GetInt32());
        Assert.Equal(1, json.GetProperty("counts").GetProperty("skippedByDecision").GetInt32());
        var item = json.GetProperty("items")[0];
        Assert.Equal("held", item.GetProperty("outcome").GetString());
        Assert.Equal("特番", item.GetProperty("filenameProgram").GetString());
    }

    [Fact]
    public void 取り込みで一部の回が失敗したら4()
    {
        Assert.Equal(ExitCodes.PartialFailure, ExitCodeRules.For(Ingest(IngestOutcome.Imported, IngestOutcome.Failed)));
    }

    [Fact]
    public void 取り込みでJellyfinの更新だけ失敗したら5()
    {
        var report = Ingest(IngestOutcome.Imported) with { LibraryRefresh = LibraryRefreshStatus.Failed, RefreshError = "503" };
        Assert.Equal(ExitCodes.ExternalFailure, ExitCodeRules.For(report));
    }

    [Fact]
    public void 回の失敗はJellyfinの更新の失敗より優先して4()
    {
        var report = Ingest(IngestOutcome.Failed) with { LibraryRefresh = LibraryRefreshStatus.Failed };
        Assert.Equal(ExitCodes.PartialFailure, ExitCodeRules.For(report));
    }

    [Fact]
    public void 取り込みでJellyfinが未設定なのは失敗にしない()
    {
        var report = Ingest(IngestOutcome.Imported) with { LibraryRefresh = LibraryRefreshStatus.NotConfigured };
        Assert.Equal(ExitCodes.Success, ExitCodeRules.For(report));
    }

    [Fact]
    public void 取り込みでJellyfinUrlの形式が不正なら1()
    {
        // 再試行では直らない設定の誤りなので、refresh と同じ 1(#191)。
        var report = Ingest(IngestOutcome.Imported) with
        {
            LibraryRefresh = LibraryRefreshStatus.Misconfigured, RefreshError = "形式が不正",
        };
        Assert.Equal(ExitCodes.UsageOrConfig, ExitCodeRules.For(report));
        Assert.Equal(ExitCodes.UsageOrConfig, ExitCodeRules.For(ScanRunReport.From(report)));
    }

    [Fact]
    public void 回の失敗はJellyfinUrlの形式不正より優先して4()
    {
        var report = Ingest(IngestOutcome.Failed) with { LibraryRefresh = LibraryRefreshStatus.Misconfigured };
        Assert.Equal(ExitCodes.PartialFailure, ExitCodeRules.For(report));
    }

    [Fact]
    public void 常駐に頼んだ走査でJellyfinの更新だけ失敗したら5()
    {
        var report = Ingest(IngestOutcome.Imported) with { LibraryRefresh = LibraryRefreshStatus.Failed, RefreshError = "503" };
        Assert.Equal(ExitCodes.ExternalFailure, ExitCodeRules.For(ScanRunReport.From(report)));
    }

    [Theory]
    [InlineData(LibraryRefreshStatus.Failed, ExitCodes.ExternalFailure)]
    [InlineData(LibraryRefreshStatus.Misconfigured, ExitCodes.UsageOrConfig)]
    [InlineData(LibraryRefreshStatus.NotConfigured, ExitCodes.Success)]
    public void 再適用のJellyfinの更新の結果は取り込みと同じ規則(LibraryRefreshStatus refresh, int expected)
    {
        var report = Reapply(ReapplyAction.Move) with { LibraryRefresh = refresh };
        Assert.Equal(expected, ExitCodeRules.For(report));
    }

    [Fact]
    public void 再適用は失敗した回があれば4で対象外だけなら0()
    {
        Assert.Equal(ExitCodes.PartialFailure, ExitCodeRules.For(Reapply(ReapplyAction.Move, ReapplyAction.Failed)));
        Assert.Equal(ExitCodes.Success, ExitCodeRules.For(Reapply(ReapplyAction.Excluded)));
    }

    [Fact]
    public async Task refreshはJellyfinが未設定なら1()
    {
        var result = await RefreshCommand.RunAsync(new NullLibraryRefresher(), CancellationToken.None);
        Assert.Equal(RefreshStatus.NotConfigured, result.Status);
        Assert.Equal(ExitCodes.UsageOrConfig, ExitCodeRules.For(result.Status));
    }

    [Fact]
    public async Task refreshはJellyfinUrlの形式不正なら1()
    {
        var result = await RefreshCommand.RunAsync(new MisconfiguredLibraryRefresher("形式が不正"), CancellationToken.None);
        Assert.Equal(RefreshStatus.Misconfigured, result.Status);
        Assert.Equal(ExitCodes.UsageOrConfig, ExitCodeRules.For(result.Status));
    }

    [Theory]
    [InlineData("jellyfin")]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task refreshはJellyfinに繋がらない_エラー_時間切れなら5(string kind)
    {
        Exception ex = kind switch
        {
            "jellyfin" => new JellyfinException("500"),
            "http" => new HttpRequestException("接続できません"),
            _ => new TaskCanceledException("時間切れ"),
        };
        var result = await RefreshCommand.RunAsync(new ThrowingRefresher(ex), CancellationToken.None);
        Assert.Equal(RefreshStatus.Failed, result.Status);
        Assert.Equal(ExitCodes.ExternalFailure, ExitCodeRules.For(result.Status));
    }

    [Fact]
    public async Task refreshは起動できれば0()
    {
        var result = await RefreshCommand.RunAsync(new ThrowingRefresher(null), CancellationToken.None);
        Assert.Equal(RefreshStatus.Started, result.Status);
        Assert.Equal(ExitCodes.Success, ExitCodeRules.For(result.Status));
    }

    // ---- 解析(UseStrictParsing と、解析の誤りを 1 にする処理) ----

    [Theory]
    [InlineData("refresh", "--config", "x.json")]
    [InlineData("ingest", "--root", @"D:\old", "--dry-run")]
    [InlineData("organize-source", "--from", @"D:\old")]
    [InlineData("ingest", "--no-such-option")]
    public async Task 削ったオプション_コマンドは使い方の誤りの1で止まり_設定を読まない(params string[] args)
    {
        var source = new CountingConfigSource();
        var (exitCode, _) = await Run(args, source);

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        Assert.Equal(0, source.Calls); // 既定の設定・台帳で動き出していない
    }

    [Fact]
    public async Task 解析の誤りもjsonなら最上位の形で出し_コマンドが決まっていれば名前を入れる()
    {
        var (exitCode, output) = await Run(["refresh", "--config", "x.json", "--json"], new CountingConfigSource());

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(CliJson.SchemaVersion, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("refresh", doc.RootElement.GetProperty("command").GetString());
        Assert.Contains("config", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task 知らないコマンドはjsonのcommandがnull()
    {
        var (exitCode, output) = await Run(["organize-source", "--json"], new CountingConfigSource());

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("command").ValueKind);
    }

    [Fact]
    public async Task コマンドの実行中にNASに届かない例外が出たら5でjsonにコマンド名と理由が出る()
    {
        var (exitCode, output) = await Run(
            ["ingest", "--dry-run", "--json"], new ThrowingConfigSource(new IOException("ネットワーク パスが見つかりません")));

        Assert.Equal(ExitCodes.ExternalFailure, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("ingest", doc.RootElement.GetProperty("command").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("ネットワーク パスが見つかりません", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task dryrunで台帳のあるフォルダに届かなければ5で_全件を予定とは答えない()
    {
        // 届かない NAS を「台帳が無い」(NullLedger)と扱うと、取り込み済みの回まで予定と誤答する(#216)。
        var dir = Directory.CreateTempSubdirectory("radirec-dryrun-unreachable-").FullName;
        try
        {
            var config = new AirshelfConfig
            {
                RadioRoot = Path.Combine(dir, "radio"),
                OutputRoot = Path.Combine(dir, "out"),
                LedgerPath = Path.Combine(dir, "nas-down", "ledger.db"),
                StationsFile = Path.Combine(dir, "stations.json"),
                ProgramsFile = Path.Combine(dir, "programs.json"),
            };
            Directory.CreateDirectory(config.RadioRoot);
            var (exitCode, output) = await Run(["ingest", "--dry-run", "--json"], new StubConfigSource(config));
            Assert.Equal(ExitCodes.ExternalFailure, exitCode);
            using var doc = JsonDocument.Parse(output);
            Assert.Equal("ingest", doc.RootElement.GetProperty("command").GetString());
            Assert.Contains(Path.Combine(dir, "nas-down"), doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("result").ValueKind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task dry_runで別の番組の判断にアルバム名が無ければ_設定の誤りとして1(bool json)
    {
        // ADR-0021。JSON として読めても中身が使えない programs.json は、壊れた JSON と同じ「設定の読み込みに失敗しました」で 1。
        var dir = Directory.CreateTempSubdirectory("radirec-dryrun-programs-").FullName;
        try
        {
            var config = new AirshelfConfig
            {
                RadioRoot = Path.Combine(dir, "radio"),
                OutputRoot = Path.Combine(dir, "out"),
                LedgerPath = Path.Combine(dir, "ledger.db"),
                StationsFile = Path.Combine(dir, "stations.json"),
                ProgramsFile = Path.Combine(dir, "programs.json"),
            };
            Directory.CreateDirectory(config.RadioRoot);
            File.WriteAllText(config.ProgramsFile, """
                { "FilenamePrograms": [ { "Match": { "Station": "TBSラジオ", "Folder": "Z", "FilenameProgram": "特番" },
                                          "Treat": "OtherProgram" } ] }
                """);
            string[] args = json ? ["ingest", "--dry-run", "--json"] : ["ingest", "--dry-run"];

            var (exitCode, output) = await Run(args, new StubConfigSource(config));

            Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
            if (json)
            {
                using var doc = JsonDocument.Parse(output);
                var error = doc.RootElement.GetProperty("error").GetString();
                Assert.StartsWith("設定の読み込みに失敗しました: ", error);
                Assert.Contains("Album", error);
                Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("result").ValueKind);
            }
            else
            {
                Assert.Contains("設定の読み込みに失敗しました", output);
                Assert.Contains("Album", output);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task コマンドの実行中の想定外の例外は1でjsonにコマンド名と理由が出る()
    {
        var (exitCode, output) = await Run(
            ["refresh", "--json"], new ThrowingConfigSource(new InvalidOperationException("想定外")));

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("refresh", doc.RootElement.GetProperty("command").GetString());
        Assert.Equal("想定外", doc.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(new[] { "ingest", "--dry-run" }, "ingest")]
    [InlineData(new[] { "--json", "reapply" }, "reapply")]
    [InlineData(new[] { "organize-source" }, null)]
    [InlineData(new[] { "--help" }, null)]
    public void 引数からコマンド名を決める(string[] args, string? expected)
    {
        Assert.Equal(expected, AirshelfCli.CommandOf(args));
    }

    [Fact]
    public async Task 版とヘルプは0()
    {
        Assert.Equal(0, (await Run(["--version"], new CountingConfigSource())).ExitCode);
        Assert.Equal(0, (await Run(["--help"], new CountingConfigSource())).ExitCode);
        Assert.Equal(0, (await Run(["ingest", "--help"], new CountingConfigSource())).ExitCode);
    }

    [Theory]
    // ingest は --dry-run のときだけ設定を読む(実行は常駐に頼み、常駐の設定で走る。#180)。
    // reapply は確認も実行も常駐に頼み、常駐の設定で走るので、ここには入らない(#181。ControlChannelTests)。
    [InlineData("ingest", "--dry-run")]
    [InlineData("refresh")]
    public async Task 設定を読めなければ1でjsonにも理由が出る(string command, params string[] options)
    {
        var (exitCode, output) = await Run([command, .. options, "--json"], new FailingConfigSource("設定ファイルが見つかりません: X"));

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(command, doc.RootElement.GetProperty("command").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("設定ファイルが見つかりません: X", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("result").ValueKind);
    }

    // ---- JSON の形 ----

    [Fact]
    public void ingestのJSONは最上位の形と件数と明細を持つ()
    {
        var report = Ingest(IngestOutcome.Imported, IngestOutcome.Failed) with
        {
            LibraryRefresh = LibraryRefreshStatus.Started,
        };
        using var doc = Parse(CliJson.Result("ingest", ExitCodeRules.For(report), IngestJson.From(report)));
        var root = doc.RootElement;

        Assert.Equal(CliJson.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ingest", root.GetProperty("command").GetString());
        Assert.Equal(4, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);

        var result = root.GetProperty("result");
        Assert.False(result.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, result.GetProperty("counts").GetProperty("imported").GetInt32());
        Assert.Equal(1, result.GetProperty("counts").GetProperty("failed").GetInt32());
        var failed = result.GetProperty("items")[1];
        Assert.Equal("failed", failed.GetProperty("outcome").GetString());
        Assert.Equal(@"I:\radio\局\番組\b.m4a", failed.GetProperty("source").GetString());
        Assert.Equal("タグを読めません", failed.GetProperty("message").GetString());
        Assert.Equal("started", result.GetProperty("libraryRefresh").GetProperty("status").GetString());
    }

    [Fact]
    public void ingestのJSONはJellyfinUrlの形式不正をmisconfiguredで出す()
    {
        var report = Ingest(IngestOutcome.Imported) with
        {
            LibraryRefresh = LibraryRefreshStatus.Misconfigured, RefreshError = "形式が不正",
        };
        using var doc = Parse(CliJson.Result("ingest", ExitCodeRules.For(report), IngestJson.From(report)));

        Assert.Equal(1, doc.RootElement.GetProperty("exitCode").GetInt32());
        var refresh = doc.RootElement.GetProperty("result").GetProperty("libraryRefresh");
        Assert.Equal("misconfigured", refresh.GetProperty("status").GetString());
        Assert.Equal("形式が不正", refresh.GetProperty("error").GetString());
    }

    [Fact]
    public void JSONは日本語をエスケープしない()
    {
        var json = CliJson.Serialize(CliJson.Failure("ingest", 1, "設定ファイルが見つかりません"));
        Assert.Contains("設定ファイルが見つかりません", json);
    }

    [Fact]
    public void reapplyのJSONはまとめ_明細_兄弟フォルダを持つ()
    {
        var report = Reapply(ReapplyAction.Move) with
        {
            UnintegratedSiblings =
            [
                new UnintegratedSiblingGroup(new SiblingFolderGroup("TBSラジオ", "ふらっと", ["ふらっと(1)", "ふらっと(2)"]), ["ふらっと(2)"]),
            ],
        };
        using var doc = Parse(CliJson.Result("reapply", ExitCodeRules.For(report), ReapplyJson.From(report, planId: null)));
        var result = doc.RootElement.GetProperty("result");

        Assert.False(result.GetProperty("aligned").GetBoolean());
        Assert.Equal(1, result.GetProperty("counts").GetProperty("move").GetInt32());
        Assert.Equal("旧", result.GetProperty("albums")[0].GetProperty("fromAlbum").GetString());
        Assert.Equal("新", result.GetProperty("albums")[0].GetProperty("toAlbum").GetString());
        var item = result.GetProperty("items")[0];
        Assert.Equal("move", item.GetProperty("action").GetString());
        Assert.Equal("2026-09-01", item.GetProperty("broadcastDate").GetString());
        var sibling = result.GetProperty("unintegratedSiblings")[0];
        Assert.Equal("TBSラジオ", sibling.GetProperty("station").GetString());
        Assert.Equal("ふらっと(2)", sibling.GetProperty("foldersWithoutSetting")[0].GetString());
        Assert.Equal("notNeeded", result.GetProperty("libraryRefresh").GetProperty("status").GetString());
    }

    [Fact]
    public void reapplyのJSONは出力先から消す回をremoveで出し_揃っていないとする()
    {
        var report = Reapply(ReapplyAction.Remove);
        using var doc = Parse(CliJson.Result("reapply", ExitCodeRules.For(report), ReapplyJson.From(report, planId: null)));
        var result = doc.RootElement.GetProperty("result");

        Assert.Equal(0, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.False(result.GetProperty("aligned").GetBoolean());
        Assert.Equal(1, result.GetProperty("counts").GetProperty("remove").GetInt32());
        Assert.Equal(1, result.GetProperty("albums")[0].GetProperty("removals").GetInt32());
        Assert.Equal("remove", result.GetProperty("items")[0].GetProperty("action").GetString());
    }

    [Fact]
    public void refreshのJSONは状態と理由を持つ()
    {
        var result = new RefreshJson(RefreshStatus.Failed, "503");
        using var doc = Parse(CliJson.Result("refresh", ExitCodeRules.For(result.Status), result));

        Assert.Equal(5, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("failed", doc.RootElement.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal("503", doc.RootElement.GetProperty("result").GetProperty("error").GetString());
    }

    // ---- 組み立て ----

    private static IngestReport Ingest(params IngestOutcome[] outcomes) => new()
    {
        DryRun = false,
        Items = outcomes.Select((o, i) => new IngestItemResult
        {
            Outcome = o,
            SourcePath = $@"I:\radio\局\番組\{(char)('a' + i)}.m4a",
            OutputPath = o == IngestOutcome.Failed ? null : $@"Z:\radio\番組\{i}.m4a",
            Message = o == IngestOutcome.Failed ? "タグを読めません" : null,
            Fingerprint = new FileFingerprint(1, 1),
        }).ToList(),
    };

    private static ReapplyReport Reapply(params ReapplyAction[] actions) => new()
    {
        DryRun = true,
        UnintegratedSiblings = [],
        Items = actions.Select(a => new ReapplyItem
        {
            Action = a,
            Station = "TBSラジオ",
            FromAlbum = "旧",
            ToAlbum = "新",
            BroadcastDate = new DateOnly(2026, 9, 1),
            FromPath = @"Z:\radio\旧\a.m4a",
            ToPath = @"Z:\radio\新\a.m4a",
            SourcePath = @"I:\radio\a.m4a",
            Reason = a == ReapplyAction.Failed ? "書けません" : null,
        }).ToList(),
    };

    private static JsonDocument Parse(CliEnvelope envelope) => JsonDocument.Parse(CliJson.Serialize(envelope));

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
        var exitCode = await AirshelfCli.RunAsync(args, "9.9.9-test", console, source);
        return (exitCode, writer.ToString());
    }

    private sealed class CountingConfigSource : ICliConfigSource
    {
        public int Calls { get; private set; }

        public bool TryLoad(out AirshelfConfig? config, out string? error)
        {
            Calls++;
            config = null;
            error = "テストでは設定を読まない";
            return false;
        }
    }

    private sealed class StubConfigSource(AirshelfConfig config) : ICliConfigSource
    {
        public bool TryLoad(out AirshelfConfig? loaded, out string? error)
        {
            loaded = config;
            error = null;
            return true;
        }
    }
    private sealed class FailingConfigSource(string message) : ICliConfigSource
    {
        public bool TryLoad(out AirshelfConfig? config, out string? error)
        {
            config = null;
            error = message;
            return false;
        }
    }

    private sealed class ThrowingConfigSource(Exception ex) : ICliConfigSource
    {
        public bool TryLoad(out AirshelfConfig? config, out string? error) => throw ex;
    }

    private sealed class ThrowingRefresher(Exception? ex) : ILibraryRefresher
    {
        public Task RefreshAsync(CancellationToken cancellationToken = default) =>
            ex is null ? Task.CompletedTask : Task.FromException(ex);
    }
}
