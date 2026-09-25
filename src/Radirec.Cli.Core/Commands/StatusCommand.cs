using System.Globalization;
using Radirec.Cli.Core.Output;
using Radirec.Hosting.Control;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Core.Commands;

/// <summary>
/// 常駐(トレイ)の状態を見る(状態取得。ADR-0020 / #179)。常駐が動いていなくても、それを答えとして終了コード 0 で返す。
/// </summary>
public sealed class StatusCommand(IAnsiConsole console, IControlClient controlClient, CliVersion cliVersion)
    : AsyncCommand<StatusCommand.Settings>
{
    public const string Name = "status";

    public sealed class Settings : CliSettings
    {
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var call = await controlClient.SendAsync(ControlProtocol.StatusCommand, cancellationToken);

        StatusJson result;
        if (call.Outcome == ControlCallOutcome.NotRunning)
        {
            // 常駐が動いていないのも答え(ADR-0020)。
            result = new StatusJson(Reachable: false, Running: false, Reason: call.Error ?? ControlPipeClient.NotRunningMessage, Tray: null);
        }
        else if (call.Outcome == ControlCallOutcome.Failed)
        {
            // 繋がったが応答を読めない(時間切れ・形が違う)。状態を尋ねるコマンドなので、これも答えとして 0 で返す(ADR-0020)。
            result = new StatusJson(Reachable: true, Running: false, Reason: call.Error ?? "常駐の応答を読めませんでした", Tray: null);
        }
        else if (ControlCommandSupport.Unanswered(call, cliVersion) is { } failure)
        {
            return CommandSupport.Fail(console, Name, settings.Json, failure.ExitCode, failure.Message);
        }
        else if (call.Response is not { Kind: ControlResponseKind.Ok, Status: { } status })
        {
            return CommandSupport.Fail(console, Name, settings.Json, ExitCodes.UsageOrConfig,
                call.Response?.Error ?? "常駐の応答に状態がありませんでした");
        }
        else
        {
            result = new StatusJson(Reachable: true, Running: status.Running, Reason: status.Running ? null : status.DegradedReason, Tray: status);
        }

        if (settings.Json)
            CliJson.Write(console, CliJson.Result(Name, ExitCodes.Success, result));
        else
            Render(result);
        return ExitCodes.Success;
    }

    private void Render(StatusJson result)
    {
        if (result.Tray is not { } tray)
        {
            console.MarkupLineInterpolated($"[yellow]{result.Reason}[/]");
            return;
        }

        if (result.Running)
            console.MarkupLine("[green]取り込み: 稼働中[/]");
        else
            console.MarkupLineInterpolated($"[yellow]取り込み: 停止中[/]{(result.Reason is { } r ? $"({r})" : string.Empty)}");

        var table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn("項目");
        table.AddColumn("値");
        Row(table, "版", tray.Version);
        Row(table, "起動", Local(tray.StartedUtc));
        Row(table, "設定ファイル", tray.ConfigPath);
        Row(table, "監視ルート", tray.RadioRoot is null ? null : tray.RadioRoot + (tray.Watching ? "" : "(監視なし・定期スキャンのみ)"));
        Row(table, "最終スキャン", tray.LastScanCompletedUtc is null
            ? null
            : $"{Local(tray.LastScanCompletedUtc)}({tray.LastScanTrigger}。取込 {tray.LastImported} / 失敗 {tray.LastFailed})");
        Row(table, "放送局マスタ", tray.StationsFile);
        Row(table, "番組の設定", tray.ProgramsFile);
        console.Write(table);
    }

    private static void Row(Table table, string label, string? value) =>
        table.AddRow(new Markup(Markup.Escape(label)), new Markup(Markup.Escape(string.IsNullOrEmpty(value) ? "—" : value)));

    private static string? Local(DateTimeOffset? utc) =>
        utc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
