using Radirec.Hosting.Control;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Commands;

/// <summary>
/// 常駐サービスのライブ状態を取得して表示する(制御チャネル。ADR-0010)。稼働状態・監視ルート・
/// 最終スキャン完了時刻とトリガ・直近の取込/失敗件数を返す。サービス未起動/未応答時は degrade する。
/// </summary>
public sealed class StatusCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ControlResponse response;
        try
        {
            response = await ControlClient.SendAsync(
                ControlContract.Commands.Status, cancellationToken: cancellationToken);
        }
        catch (ControlUnavailableException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }

        if (!response.Ok)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]状態取得に失敗しました:[/] {response.Error}");
            return 1;
        }

        var status = response.ResultAs<ServiceStatusSnapshot>();
        if (status is null)
        {
            AnsiConsole.MarkupLine("[red]状態の応答を解析できませんでした。[/]");
            return 1;
        }

        Render(status);
        return 0;
    }

    private static void Render(ServiceStatusSnapshot s)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("項目");
        table.AddColumn("値");

        table.AddRow("版", Markup.Escape(s.Version));
        table.AddRow("起動時刻", s.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        table.AddRow("監視ルート", Markup.Escape(s.RadioRoot));
        table.AddRow("監視状態", s.Watching ? "[green]監視中[/]" : "[yellow]定期スキャンのみ(ルート不在)[/]");
        table.AddRow(
            "最終スキャン",
            s.LastScanCompletedUtc is { } t
                ? $"{t.ToLocalTime():yyyy-MM-dd HH:mm:ss}({Markup.Escape(s.LastScanTrigger ?? "-")})"
                : "[grey]未走査[/]");
        table.AddRow("直近の取込 / 失敗", $"{s.LastImported} / {s.LastFailed}");

        AnsiConsole.Write(table);
    }
}
