using Radirec.Hosting.Control;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Commands;

/// <summary>
/// 常駐サービスへ設定リロードを要求する(制御チャネル。ADR-0010)。サービスが `radirec.json` を再読込し、
/// config 由来ランタイムを丸ごと再構築する。**受理 = 構文検証通過 + 再構築キュー投入まで**で、実際の
/// 反映は在中スキャン完了後(不正 config は旧構成を維持)。
/// </summary>
public sealed class ReloadCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        try
        {
            var response = await ControlClient.SendAsync(
                ControlContract.Commands.Reload, cancellationToken: cancellationToken);
            if (!response.Ok)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]設定リロードが拒否されました:[/] {response.Error}");
                return 1;
            }

            AnsiConsole.MarkupLine(
                "[green]設定リロードを受理しました。[/][grey](反映は在中スキャン完了後。不正 config は旧構成を維持)[/]");
            return 0;
        }
        catch (ControlUnavailableException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
