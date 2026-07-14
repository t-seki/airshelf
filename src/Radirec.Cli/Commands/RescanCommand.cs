using Radirec.Hosting.Control;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Commands;

/// <summary>
/// 常駐サービスへ手動再スキャンを要求する(制御チャネル。ADR-0010)。自動トリガを待たず今すぐ 1 回
/// 走査させる。サービスは要求を coalesce するので「受理」まで(スキャン完了は待たない)。
/// </summary>
public sealed class RescanCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        try
        {
            var response = await ControlClient.SendAsync(
                ControlContract.Commands.Rescan, cancellationToken: cancellationToken);
            if (!response.Ok)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]再スキャン要求が拒否されました:[/] {response.Error}");
                return 1;
            }

            AnsiConsole.MarkupLine("[green]再スキャンを要求しました。[/]");
            return 0;
        }
        catch (ControlUnavailableException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
