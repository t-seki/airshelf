using System.IO.Pipes;
using System.Text.Json;
using Radirec.Cli.Core;
using Radirec.Core.Configuration;
using Radirec.Core.Ingestion;
using Radirec.Core.Reapply;
using Radirec.Hosting.Control;
using Spectre.Console;

namespace Radirec.Tests;

/// <summary>
/// 制御チャネル(CLI → トレイの常駐。ADR-0020 / #179)の往復。named pipe を実際に使う(Linux では Unix ドメインソケット)。
/// 名前はテストごとに一意にする(既定のユーザーごとの名前は使わない)。
/// <para>
/// 接続の時間切れ・わざと止めた設定リロードでスレッドプールのスレッドを持つので、ほかのテストと並べて走らせない
/// (<see cref="ControlChannelCollection"/>)。並べると、スレッドプールが増えた分、タイミングに依るほかのテストの結果が変わりうる。
/// </para>
/// </summary>
[Collection(ControlChannelCollection.Name)]
public sealed class ControlChannelTests
{
    private static readonly TimeSpan ShortConnect = TimeSpan.FromMilliseconds(300);

    // ---- パイプ名 ----

    [Fact]
    public void パイプ名はユーザーごとで_使えない文字は置き換える()
    {
        Assert.Equal("radirec-control-S-1-5-21-1", ControlProtocol.PipeNameFor("S-1-5-21-1"));
        Assert.Equal("radirec-control-DOMAIN_user", ControlProtocol.PipeNameFor(@"DOMAIN\user"));
        Assert.StartsWith(ControlProtocol.PipeNamePrefix, ControlProtocol.DefaultPipeName());
    }

    // ---- 往復 ----

    [Fact]
    public async Task statusは取り込みワーカーの状態と設定ファイルの場所を返す()
    {
        var control = new FakeControl();
        await using var server = StartServer(control, degradedReason: null);

        var result = await Client(server).SendAsync(ControlProtocol.StatusCommand, CancellationToken.None);

        Assert.Equal(ControlCallOutcome.Answered, result.Outcome);
        Assert.Equal("1.2.3", result.TrayVersion);
        var status = result.Response!.Status!;
        Assert.True(status.Running);
        Assert.Equal(@"C:\data\radirec.json", status.ConfigPath);
        Assert.Equal(@"I:\radio", status.RadioRoot);
        Assert.Equal(3, status.LastImported);
        Assert.Equal("手動", status.LastScanTrigger);
    }

    [Fact]
    public async Task 縮退中のstatusは止まっていることと理由を返す()
    {
        await using var server = StartServer(control: null, degradedReason: "設定を読み込めませんでした");

        var result = await Client(server).SendAsync(ControlProtocol.StatusCommand, CancellationToken.None);

        Assert.Equal(ControlCallOutcome.Answered, result.Outcome);
        var status = result.Response!.Status!;
        Assert.False(status.Running);
        Assert.Equal("設定を読み込めませんでした", status.DegradedReason);
        Assert.Equal(@"C:\data\radirec.json", status.ConfigPath);
        Assert.Null(status.RadioRoot);
    }

    [Fact]
    public async Task reloadは受理と拒否をそのまま返す()
    {
        var control = new FakeControl();
        await using var server = StartServer(control, degradedReason: null);

        var accepted = await Client(server).SendAsync(ControlProtocol.ReloadCommand, CancellationToken.None);
        Assert.True(accepted.Response!.Reload!.Accepted);

        control.Reload = ReloadResult.Failed("programs.json の JSON が壊れています");
        var rejected = await Client(server).SendAsync(ControlProtocol.ReloadCommand, CancellationToken.None);
        Assert.Equal(ControlResponseKind.Ok, rejected.Response!.Kind);
        Assert.False(rejected.Response.Reload!.Accepted);
        Assert.Equal("programs.json の JSON が壊れています", rejected.Response.Reload.Error);
        Assert.Equal(2, control.ReloadCalls);
    }

    [Fact]
    public async Task 縮退中のreloadは縮退と理由を返す()
    {
        await using var server = StartServer(control: null, degradedReason: "取り込みの起動に失敗しました(設定ページ参照)");

        var result = await Client(server).SendAsync(ControlProtocol.ReloadCommand, CancellationToken.None);

        Assert.Equal(ControlResponseKind.Degraded, result.Response!.Kind);
        Assert.Equal("取り込みの起動に失敗しました(設定ページ参照)", result.Response.Error);
    }

    [Fact]
    public async Task ワーカーが起動後に止まったらstatusは理由を返し_reloadは縮退にして頼まない()
    {
        var control = new FakeControl { Running = false };
        await using var server = StartServer(control, degradedReason: null);

        var status = (await Client(server).SendAsync(ControlProtocol.StatusCommand, CancellationToken.None)).Response!.Status!;
        Assert.False(status.Running);
        Assert.Equal(ControlPipeServer.WorkerStoppedReason, status.DegradedReason);
        Assert.Equal(@"I:\radio", status.RadioRoot); // 止まる前の状態は見せる

        var reload = (await Client(server).SendAsync(ControlProtocol.ReloadCommand, CancellationToken.None)).Response!;
        Assert.Equal(ControlResponseKind.Degraded, reload.Kind);
        Assert.Equal(ControlPipeServer.WorkerStoppedReason, reload.Error);
        Assert.Equal(0, control.ReloadCalls);
    }

    [Fact]
    public async Task CLIはワーカーが止まっていればstatusは0で理由_reloadは2()
    {
        await using var server = StartServer(new FakeControl { Running = false }, degradedReason: null);

        var (statusCode, statusOut) = await RunCli(["status", "--json"], Client(server));
        Assert.Equal(ExitCodes.Success, statusCode);
        using (var doc = JsonDocument.Parse(statusOut))
        {
            Assert.False(doc.RootElement.GetProperty("result").GetProperty("running").GetBoolean());
            Assert.Equal(ControlPipeServer.WorkerStoppedReason, doc.RootElement.GetProperty("result").GetProperty("reason").GetString());
        }

        Assert.Equal(ExitCodes.ResidentNotRunning, (await RunCli(["reload"], Client(server))).ExitCode);
    }

    [Fact]
    public async Task 起動処理で止まったら_statusとreloadの理由は起動処理の失敗と分かる文言にする()
    {
        // #217:起動処理(孤児の掃除・監視の開始)の失敗を、ループの異常終了(WorkerStoppedReason)と見分けられるようにする。
        var control = new FakeControl { Running = false, StartFailure = @"Access to the path 'C:\out\locked' is denied." };
        await using var server = StartServer(control, degradedReason: null);
        var expected = @"取り込みの起動処理に失敗しました: Access to the path 'C:\out\locked' is denied.(gui.log 参照 → 原因を直して再起動)";
        var (statusCode, statusOut) = await RunCli(["status", "--json"], Client(server));
        Assert.Equal(ExitCodes.Success, statusCode);
        using (var doc = JsonDocument.Parse(statusOut))
        {
            Assert.False(doc.RootElement.GetProperty("result").GetProperty("running").GetBoolean());
            Assert.Equal(expected, doc.RootElement.GetProperty("result").GetProperty("reason").GetString());
        }
        var reload = (await Client(server).SendAsync(ControlProtocol.ReloadCommand, CancellationToken.None)).Response!;
        Assert.Equal(ControlResponseKind.Degraded, reload.Kind);
        Assert.Equal(expected, reload.Error);
        Assert.Equal(0, control.ReloadCalls);
    }

    [Fact]
    public async Task CLIのstatusは応答を読めなくても0で_running_falseと理由を返す_reloadは2()
    {
        var failed = new FixedClient(new ControlCallResult(ControlCallOutcome.Failed, null, "常駐から応答がありません(時間切れ)"));

        var (exitCode, output) = await RunCli(["status", "--json"], failed);
        Assert.Equal(ExitCodes.Success, exitCode);
        using (var doc = JsonDocument.Parse(output))
        {
            var result = doc.RootElement.GetProperty("result");
            Assert.True(result.GetProperty("reachable").GetBoolean());
            Assert.False(result.GetProperty("running").GetBoolean());
            Assert.Equal("常駐から応答がありません(時間切れ)", result.GetProperty("reason").GetString());
        }

        Assert.Equal(ExitCodes.ResidentNotRunning, (await RunCli(["reload"], failed)).ExitCode);
    }

    [Fact]
    public async Task 応答を読まずに切る相手がいても次の接続に応える()
    {
        await using var server = StartServer(new FakeControl(), degradedReason: null);

        // 要求だけ送って応答を読まずに切る。
        // CLI と同じ作り方にする(素の CurrentUserOnly だと、昇格した Windows のプロセスで走らせたとき所有者の比べ方が違い拒まれる。#214)。
        await using (var rude = ControlPipeSecurity.CreateClient(server.PipeName))
        {
            await rude.ConnectAsync(5000);
            var bytes = ControlProtocol.LineEncoding.GetBytes("""{"protocolVersion":1,"command":"status"}""" + "\n");
            await rude.WriteAsync(bytes);
        }

        var result = await Client(server).SendAsync(ControlProtocol.StatusCommand, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ControlCallOutcome.Answered, result.Outcome);
    }

    [Fact]
    public async Task 版の違う要求には版の不一致を返して何もしない()
    {
        var control = new FakeControl();
        await using var server = StartServer(control, degradedReason: null);

        var result = await Client(server).SendAsync(new ControlRequest(99, ControlProtocol.ReloadCommand), CancellationToken.None);

        Assert.Equal(ControlCallOutcome.VersionMismatch, result.Outcome);
        Assert.Equal("1.2.3", result.TrayVersion);
        Assert.Equal(0, control.ReloadCalls);
    }

    [Fact]
    public async Task 版の違う応答は形を読まずに版の不一致にする()
    {
        var name = UniqueName();
        var fake = Task.Run(async () =>
        {
            // 受け口と同じ作り方にする(所有者がユーザーの SID でないと、CLI の所有者の確かめで拒まれる。#214)。
            await using var pipe = ControlPipeSecurity.CreateServer(name);
            await pipe.WaitForConnectionAsync();
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await reader.ReadLineAsync();
            await using var writer = new StreamWriter(pipe, ControlProtocol.LineEncoding, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("""{"protocolVersion":99,"trayVersion":"9.0.0","somethingNew":true}""");
        });

        var result = await new ControlPipeClient(name, TimeSpan.FromSeconds(2)).SendAsync(ControlProtocol.StatusCommand, CancellationToken.None);
        await fake;

        Assert.Equal(ControlCallOutcome.VersionMismatch, result.Outcome);
        Assert.Equal("9.0.0", result.TrayVersion);
    }

    [Fact]
    public async Task 受け口が無ければ繋がらないとして返す()
    {
        var result = await new ControlPipeClient(UniqueName(), ShortConnect).SendAsync(ControlProtocol.StatusCommand, CancellationToken.None);

        Assert.Equal(ControlCallOutcome.NotRunning, result.Outcome);
        Assert.Equal(ControlPipeClient.NotRunningMessage, result.Error);
    }

    [Fact]
    public async Task 知らないコマンドは読めない要求として返す()
    {
        await using var server = StartServer(new FakeControl(), degradedReason: null);

        var result = await Client(server).SendAsync("rescan", CancellationToken.None);

        Assert.Equal(ControlResponseKind.BadRequest, result.Response!.Kind);
    }

    [Fact]
    public async Task 遅い要求の処理中も次の接続に応える()
    {
        var control = new FakeControl { BlockReload = new ManualResetEventSlim(false) };
        await using var server = StartServer(control, degradedReason: null);

        var slow = Client(server).SendAsync(ControlProtocol.ReloadCommand, CancellationToken.None);
        await control.ReloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var status = await Client(server).SendAsync(ControlProtocol.StatusCommand, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ControlCallOutcome.Answered, status.Outcome);
        Assert.False(slow.IsCompleted);

        control.BlockReload.Set();
        Assert.True((await slow).Response!.Reload!.Accepted);
    }

    // ---- CLI(radirec status / reload) ----

    [Fact]
    public async Task CLIのstatusは常駐に繋がらなくても0で_running_falseと理由を返す()
    {
        var (exitCode, output) = await RunCli(["status", "--json"], new ControlPipeClient(UniqueName(), ShortConnect));

        Assert.Equal(ExitCodes.Success, exitCode);
        using var doc = JsonDocument.Parse(output);
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("status", doc.RootElement.GetProperty("command").GetString());
        Assert.False(result.GetProperty("reachable").GetBoolean());
        Assert.False(result.GetProperty("running").GetBoolean());
        Assert.Equal(ControlPipeClient.NotRunningMessage, result.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("tray").ValueKind);
    }

    [Fact]
    public async Task CLIのstatusは常駐の状態をjsonで返す()
    {
        await using var server = StartServer(new FakeControl(), degradedReason: null);

        var (exitCode, output) = await RunCli(["status", "--json"], Client(server));

        Assert.Equal(ExitCodes.Success, exitCode);
        using var doc = JsonDocument.Parse(output);
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("reachable").GetBoolean());
        Assert.True(result.GetProperty("running").GetBoolean());
        Assert.Equal(@"C:\data\radirec.json", result.GetProperty("tray").GetProperty("configPath").GetString());
        Assert.Equal(@"I:\radio", result.GetProperty("tray").GetProperty("radioRoot").GetString());
    }

    [Fact]
    public async Task CLIのstatusは縮退中も0で理由を返す()
    {
        await using var server = StartServer(control: null, degradedReason: "設定を読み込めませんでした");

        var (exitCode, output) = await RunCli(["status", "--json"], Client(server));

        Assert.Equal(ExitCodes.Success, exitCode);
        using var doc = JsonDocument.Parse(output);
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("reachable").GetBoolean());
        Assert.False(result.GetProperty("running").GetBoolean());
        Assert.Equal("設定を読み込めませんでした", result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task CLIのreloadは受理で0_拒否で1()
    {
        var control = new FakeControl();
        await using var server = StartServer(control, degradedReason: null);

        Assert.Equal(ExitCodes.Success, (await RunCli(["reload"], Client(server))).ExitCode);

        control.Reload = ReloadResult.Failed("radirec.json を読めません");
        var (exitCode, output) = await RunCli(["reload", "--json"], Client(server));
        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("result").GetProperty("accepted").GetBoolean());
        Assert.Equal("radirec.json を読めません", doc.RootElement.GetProperty("result").GetProperty("error").GetString());
    }

    [Fact]
    public async Task CLIのreloadは縮退中なら2で理由を添える()
    {
        await using var server = StartServer(control: null, degradedReason: "設定を読み込めませんでした");

        var (exitCode, output) = await RunCli(["reload", "--json"], Client(server));

        Assert.Equal(ExitCodes.ResidentNotRunning, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Contains("設定を読み込めませんでした", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CLIのreloadは常駐に繋がらなければ2()
    {
        var (exitCode, output) = await RunCli(["reload", "--json"], new ControlPipeClient(UniqueName(), ShortConnect));

        Assert.Equal(ExitCodes.ResidentNotRunning, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(ControlPipeClient.NotRunningMessage, doc.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("reload")]
    public async Task CLIは版が違えば1で_トレイとCLIの版を知らせる(string command)
    {
        var client = new FixedClient(new ControlCallResult(ControlCallOutcome.VersionMismatch, null, "プロトコルの版が違います", "2.0.0"));

        var (exitCode, output) = await RunCli([command, "--json"], client);

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("トレイと CLI の版が違います(トレイ v2.0.0 / CLI v9.9.9-test)", doc.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(new[] { "status" }, "status")]
    [InlineData(new[] { "--json", "reload" }, "reload")]
    public void 引数からstatusとreloadのコマンド名も決める(string[] args, string expected)
    {
        Assert.Equal(expected, RadirecCli.CommandOf(args));
    }

    // ---- 走査の実行(radirec ingest。#180)----

    [Fact]
    public async Task scanは走査が終わるまで待って結果を返す_応答の待ち時間は走査の実行用のものを使う()
    {
        var control = new FakeControl { BlockScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var server = StartServer(control, degradedReason: null);
        // 状態取得などの応答の待ち時間は短くしても、走査の実行は走査が終わるまで待つ。
        var client = new ControlPipeClient(server.PipeName, TimeSpan.FromSeconds(5), responseTimeout: TimeSpan.FromMilliseconds(200));

        var call = client.SendAsync(ControlProtocol.ScanCommand, CancellationToken.None);
        await Task.Delay(600);
        Assert.False(call.IsCompleted);

        control.BlockScan.SetResult();
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ControlCallOutcome.Answered, result.Outcome);
        var report = result.Response!.ScanRun!.Report!;
        Assert.Equal(1, report.Count(IngestOutcome.Imported));
        Assert.Equal(@"Z:\out\a.m4a", report.Items.Single().OutputPath);
        Assert.Equal(1, control.ScanCalls);
    }

    [Fact]
    public async Task 走査の実行が時間内に終わらなければ応答を読めないとして返す()
    {
        var control = new FakeControl { BlockScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var server = StartServer(control, degradedReason: null);
        var client = new ControlPipeClient(server.PipeName, TimeSpan.FromSeconds(5), scanResponseTimeout: TimeSpan.FromMilliseconds(300));

        var result = await client.SendAsync(ControlProtocol.ScanCommand, CancellationToken.None);
        control.BlockScan.SetResult();

        Assert.Equal(ControlCallOutcome.Failed, result.Outcome);
        Assert.Contains("走査が時間内に終わりませんでした", result.Error);
    }

    [Fact]
    public async Task CLIのingestは常駐に走査を頼み_結果をjsonで返す()
    {
        var control = new FakeControl();
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["ingest", "--json"], Client(server));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(1, control.ScanCalls);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("ingest", doc.RootElement.GetProperty("command").GetString());
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, result.GetProperty("counts").GetProperty("imported").GetInt32());
        Assert.Equal("started", result.GetProperty("libraryRefresh").GetProperty("status").GetString());
        Assert.Equal(@"I:\radio\a.m4a", result.GetProperty("items")[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task CLIのingestは一部の回が失敗したら4()
    {
        var control = new FakeControl
        {
            ScanRun = ScanRunResult.Ok(new ScanRunReport
            {
                DryRun = false,
                Items =
                [
                    new ScanRunItem(IngestOutcome.Imported, "a.m4a", "out/a.m4a", null, false),
                    new ScanRunItem(IngestOutcome.Failed, "b.m4a", null, "日付を読めません", false),
                ],
            }),
        };
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["ingest"], Client(server));

        Assert.Equal(ExitCodes.PartialFailure, exitCode);
        Assert.Contains("b.m4a", output);
    }

    [Fact]
    public async Task CLIのingestは常駐に繋がらなければ2で_自分では走査しない()
    {
        var (exitCode, output) = await RunCli(["ingest", "--json"], new ControlPipeClient(UniqueName(), ShortConnect));

        Assert.Equal(ExitCodes.ResidentNotRunning, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(ControlPipeClient.NotRunningMessage, doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CLIのingestは取り込みが止まっていれば2で理由を添え_走査を頼まない()
    {
        var control = new FakeControl { Running = false };
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["ingest", "--json"], Client(server));

        Assert.Equal(ExitCodes.ResidentNotRunning, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Contains(ControlPipeServer.WorkerStoppedReason, doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, control.ScanCalls);
    }

    [Theory]
    [InlineData(ScanRunFailure.Stopped, ExitCodes.ResidentNotRunning)]
    [InlineData(ScanRunFailure.External, ExitCodes.ExternalFailure)]
    [InlineData(ScanRunFailure.Internal, ExitCodes.UsageOrConfig)]
    public async Task CLIのingestは走査が失敗したら種類ごとの終了コードで理由を返す(ScanRunFailure failure, int expected)
    {
        var control = new FakeControl { ScanRun = ScanRunResult.Failed(failure, "走査に失敗しました: 理由") };
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["ingest", "--json"], Client(server));

        Assert.Equal(expected, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("走査に失敗しました: 理由", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CLIのingestは走査の実行を知らない古いトレイなら1()
    {
        var oldTray = new FixedClient(new ControlCallResult(ControlCallOutcome.Answered, new ControlResponse
        {
            ProtocolVersion = ControlProtocol.Version,
            TrayVersion = "0.9.0",
            Kind = ControlResponseKind.BadRequest,
            Error = "知らないコマンドです: scan",
        }, null, "0.9.0"));

        var (exitCode, output) = await RunCli(["ingest", "--json"], oldTray);

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Contains("トレイが古ければ更新してください", doc.RootElement.GetProperty("error").GetString());
    }

    // ---- 再適用(radirec reapply。#181)----

    private static ReapplyReport Plan(bool dryRun, params string[] fromPaths) => new()
    {
        DryRun = dryRun,
        Items = [.. fromPaths.Select(from => new ReapplyItem
        {
            Action = ReapplyAction.Move,
            Station = "TBSラジオ",
            FromAlbum = "安住紳一郎の日曜天国(1)",
            ToAlbum = "安住紳一郎の日曜天国",
            BroadcastDate = new DateOnly(2026, 9, 20),
            BroadcastTime = new TimeOnly(10, 0),
            FromPath = from,
            ToPath = @"Z:\out\安住紳一郎の日曜天国\" + from,
            SourcePath = @"I:\radio\" + from,
        })],
        UnintegratedSiblings = [new UnintegratedSiblingGroup(new("TBSラジオ", "a", ["a(1)", "a(2)"]), ["a(2)"])],
    };

    [Fact]
    public async Task reapplyは予定とIDを運ぶ_予定は往復しても同じIDになる()
    {
        var plan = Plan(dryRun: true, "a.m4a", "b.m4a");
        var control = new FakeControl { ReapplyPlan = ReapplyResult.Planned(plan) };
        await using var server = StartServer(control, degradedReason: null);

        var result = await Client(server).SendAsync(
            new ControlRequest(ControlProtocol.Version, ControlProtocol.ReapplyCommand), CancellationToken.None);

        var reapply = result.Response!.Reapply!;
        Assert.Equal(ReapplyPlanId.Of(plan), reapply.PlanId);
        Assert.Equal(ReapplyPlanId.Of(plan), ReapplyPlanId.Of(reapply.Report!)); // 明細は値のまま届く
        Assert.True(reapply.Report!.HasSamePlanAs(plan));
        Assert.Equal("a(2)", Assert.Single(reapply.Report.UnintegratedSiblings).FoldersWithoutSetting[0]);
        Assert.Equal(1, control.ReapplyPlanCalls);
        Assert.Null(control.AppliedPlanId);
    }

    [Fact]
    public async Task CLIのreapplyはオプションなしで確認だけを頼み_予定IDと実行の仕方を出す()
    {
        var plan = Plan(dryRun: true, "a.m4a");
        var control = new FakeControl { ReapplyPlan = ReapplyResult.Planned(plan) };
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["reapply"], Client(server));

        Assert.Equal(ExitCodes.Success, exitCode);
        var id = ReapplyPlanId.Of(plan);
        Assert.Contains($"予定 ID: {id}", output);
        Assert.Contains($"radirec reapply --apply {id}", output);
        Assert.Null(control.AppliedPlanId);
    }

    [Fact]
    public async Task CLIのreapplyのdry_runはオプションなしと同じで_jsonにplanIdを出す()
    {
        var plan = Plan(dryRun: true, "a.m4a");
        var control = new FakeControl { ReapplyPlan = ReapplyResult.Planned(plan) };
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["reapply", "--dry-run", "--json"], Client(server));

        Assert.Equal(ExitCodes.Success, exitCode);
        using var doc = JsonDocument.Parse(output);
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(ReapplyPlanId.Of(plan), result.GetProperty("planId").GetString());
        Assert.True(result.GetProperty("dryRun").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("error").ValueKind);
        Assert.Null(control.AppliedPlanId);
    }

    [Fact]
    public async Task CLIのreapply_applyは予定IDを添えて頼み_一致すれば実行の結果を返す()
    {
        var id = ReapplyPlanId.Of(Plan(dryRun: true, "a.m4a"));
        var control = new FakeControl { ReapplyApply = ReapplyResult.Ok(Plan(dryRun: false, "a.m4a")) };
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["reapply", "--apply", id.ToUpperInvariant(), "--json"], Client(server));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(id, control.AppliedPlanId); // 小文字にして頼む
        using var doc = JsonDocument.Parse(output);
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("dryRun").GetBoolean());
        Assert.Equal(id, result.GetProperty("planId").GetString());
    }

    [Fact]
    public async Task CLIのreapply_applyは予定が変わっていれば3で_新しい予定とIDと理由を返す()
    {
        var oldId = ReapplyPlanId.Of(Plan(dryRun: true, "a.m4a"));
        var replanned = Plan(dryRun: true, "a.m4a", "b.m4a");
        var control = new FakeControl { ReapplyApply = ReapplyResult.PlanChanged("確かめた予定から変わったため実行しませんでした", replanned) };
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["reapply", "--apply", oldId, "--json"], Client(server));

        Assert.Equal(ExitCodes.PlanChanged, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(3, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("変わったため実行しませんでした", doc.RootElement.GetProperty("error").GetString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal(ReapplyPlanId.Of(replanned), result.GetProperty("planId").GetString());
        Assert.Equal(2, result.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task CLIのreapply_applyは予定が変わっていれば表でも新しい予定IDを出す()
    {
        var replanned = Plan(dryRun: true, "b.m4a");
        var control = new FakeControl { ReapplyApply = ReapplyResult.PlanChanged("確かめた予定から変わったため実行しませんでした", replanned) };
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["reapply", "--apply", "0123456789abcdef"], Client(server));

        Assert.Equal(ExitCodes.PlanChanged, exitCode);
        Assert.Contains("変わったため実行しませんでした", output);
        Assert.Contains($"radirec reapply --apply {ReapplyPlanId.Of(replanned)}", output);
    }

    [Theory]
    [InlineData("0123456789abcde")]
    [InlineData("0123456789abcdefg")]
    [InlineData("0123456789abcdeg")]
    public async Task CLIのreapply_applyは予定IDの形が違えば1で_常駐に頼まない(string id)
    {
        var control = new FakeControl();
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["reapply", "--apply", id, "--json"], Client(server));

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Contains("予定 ID の形が違います", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, control.ReapplyPlanCalls);
        Assert.Null(control.AppliedPlanId);
    }

    [Fact]
    public async Task CLIのreapplyはdry_runとapplyを一緒に指定すれば1()
    {
        var control = new FakeControl();
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, _) = await RunCli(["reapply", "--dry-run", "--apply", "0123456789abcdef"], Client(server));

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        Assert.Null(control.AppliedPlanId);
    }

    [Fact]
    public async Task CLIのreapplyは常駐に繋がらなければ2で_自分では走らせない()
    {
        var (exitCode, output) = await RunCli(["reapply", "--json"], new ControlPipeClient(UniqueName(), ShortConnect));

        Assert.Equal(ExitCodes.ResidentNotRunning, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(ControlPipeClient.NotRunningMessage, doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CLIのreapplyは取り込みが止まっていれば2で理由を添え_頼まない()
    {
        var control = new FakeControl { Running = false };
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["reapply", "--json"], Client(server));

        Assert.Equal(ExitCodes.ResidentNotRunning, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Contains(ControlPipeServer.WorkerStoppedReason, doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, control.ReapplyPlanCalls);
    }

    [Fact]
    public async Task CLIのreapplyは常駐のジョブが止まって中止したら2()
    {
        var control = new FakeControl { ReapplyPlan = ReapplyResult.Stopped("取り込みが停止したため中止しました") };
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["reapply", "--json"], Client(server));

        Assert.Equal(ExitCodes.ResidentNotRunning, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("取り込みが停止したため中止しました", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CLIのreapplyは設定を読めなければ1で理由を返す()
    {
        var control = new FakeControl
        {
            ReapplyApply = ReapplyResult.Failed("設定の読み込みに失敗しました: programs.json の JSON が壊れています"),
        };
        await using var server = StartServer(control, degradedReason: null);

        var (exitCode, output) = await RunCli(["reapply", "--apply", "0123456789abcdef", "--json"], Client(server));

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Contains("programs.json の JSON が壊れています", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CLIのreapplyは再適用を知らない古いトレイなら1で_更新を促す()
    {
        var oldTray = new FixedClient(new ControlCallResult(ControlCallOutcome.Answered, new ControlResponse
        {
            ProtocolVersion = ControlProtocol.Version,
            TrayVersion = "0.9.0",
            Kind = ControlResponseKind.BadRequest,
            Error = "知らないコマンドです: reapply",
        }, null, "0.9.0"));

        var (exitCode, output) = await RunCli(["reapply", "--json"], oldTray);

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var doc = JsonDocument.Parse(output);
        Assert.Contains("トレイが古ければ更新してください", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void 古いトレイは予定IDのフィールドを読み飛ばし_コマンドを読める()
    {
        // 古いトレイの要求の型(PlanId を持たない)で、新しい CLI の要求を読めることを確かめる(読めれば、知らないコマンドとして返す)。
        var line = JsonSerializer.Serialize(
            new ControlRequest(ControlProtocol.Version, ControlProtocol.ReapplyCommand, "0123456789abcdef"), ControlProtocol.JsonOptions);

        var old = JsonSerializer.Deserialize<OldControlRequest>(line, ControlProtocol.JsonOptions);

        Assert.Equal(ControlProtocol.ReapplyCommand, old!.Command);
        Assert.Equal(ControlProtocol.Version, old.ProtocolVersion);
    }

    private sealed record OldControlRequest(int ProtocolVersion, string Command);

    // ---- 組み立て ----

    private static string UniqueName() => "radirec-test-" + Guid.NewGuid().ToString("N");

    private static ControlPipeServer StartServer(IServiceControl? control, string? degradedReason)
    {
        var server = new ControlPipeServer(UniqueName(), control, degradedReason, @"C:\data\radirec.json", "1.2.3");
        server.Start();
        return server;
    }

    // 受け口のタスクが最初のパイプを作るまでの間も、クライアントは接続をやり直すので待てる。
    private static ControlPipeClient Client(ControlPipeServer server) => new(server.PipeName, TimeSpan.FromSeconds(5));

    private static async Task<(int ExitCode, string Output)> RunCli(string[] args, IControlClient client)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        var exitCode = await RadirecCli.RunAsync(args, "9.9.9-test", console, controlClient: client);
        return (exitCode, writer.ToString());
    }

    private sealed class FixedClient(ControlCallResult result) : IControlClient
    {
        public Task<ControlCallResult> SendAsync(string command, CancellationToken cancellationToken) => Task.FromResult(result);

        public Task<ControlCallResult> SendAsync(ControlRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class FakeControl : IServiceControl
    {
        public ReloadResult Reload { get; set; } = ReloadResult.Ok();
        public bool Running { get; init; } = true;
        public string? StartFailure { get; init; }
        public int ReloadCalls { get; private set; }
        public ManualResetEventSlim? BlockReload { get; init; }
        public TaskCompletionSource ReloadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ServiceStatusSnapshot GetStatus() => new()
        {
            Version = "1.2.3",
            StartedUtc = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero),
            RadioRoot = @"I:\radio",
            Watching = true,
            Running = Running,
            StartFailure = StartFailure,
            LastScanCompletedUtc = new DateTimeOffset(2026, 9, 26, 1, 0, 0, TimeSpan.Zero),
            LastScanTrigger = "手動",
            LastImported = 3,
            StationsFile = @"C:\data\stations.json",
            ProgramsFile = @"C:\data\programs.json",
        };

        public ReloadResult RequestReload()
        {
            ReloadCalls++;
            ReloadEntered.TrySetResult();
            BlockReload?.Wait(TimeSpan.FromSeconds(10));
            return Reload;
        }

        public ScanRunResult ScanRun { get; set; } = ScanRunResult.Ok(new ScanRunReport
        {
            DryRun = false,
            Items = [new ScanRunItem(IngestOutcome.Imported, @"I:\radio\a.m4a", @"Z:\out\a.m4a", null, false)],
            LibraryRefresh = LibraryRefreshStatus.Started,
        });

        public int ScanCalls { get; private set; }

        /// <summary>設定すると、走査の実行はこれが完了するまで終わらない(走査に時間がかかる)。</summary>
        public TaskCompletionSource? BlockScan { get; init; }

        public async Task<ScanRunResult> RunScanAsync()
        {
            ScanCalls++;
            if (BlockScan is { } block)
                await block.Task;
            return ScanRun;
        }

        public void RequestRescan() => throw new NotSupportedException();
        public Task<ReapplyResult> PlanReapplyAsync() => throw new NotSupportedException();
        public Task<ReapplyResult> PlanReapplyAsync(ProgramMasterConfig programs) => throw new NotSupportedException();
        public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
        public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();

        public ReapplyResult ReapplyPlan { get; init; } = ReapplyResult.Failed("確認は用意していません");
        public ReapplyResult ReapplyApply { get; init; } = ReapplyResult.Failed("実行は用意していません");
        public int ReapplyPlanCalls { get; private set; }
        public string? AppliedPlanId { get; private set; }

        public Task<ReapplyResult> PlanReapplyForCliAsync()
        {
            ReapplyPlanCalls++;
            return Task.FromResult(ReapplyPlan);
        }

        public Task<ReapplyResult> ApplyReapplyPlanAsync(string planId)
        {
            AppliedPlanId = planId;
            return Task.FromResult(ReapplyApply);
        }
    }
}

/// <summary>制御チャネルのテストを、ほかのテストと並べずに走らせる(<see cref="ControlChannelTests"/>)。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ControlChannelCollection
{
    public const string Name = "制御チャネル(並べない)";
}
