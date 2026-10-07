using Airshelf.Cli.Core.Output;
using Airshelf.Hosting.Control;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Airshelf.Cli.Core.Commands;

/// <summary>
/// 常駐に設定リロードを頼む(ADR-0020 / #179)。受理は 0、設定が不正で拒否は 1、常駐が動いていない・取り込みが止まっている
/// (縮退)ときは 2。縮退中にトレイを再起動することはしない。
/// </summary>
public sealed class ReloadCommand(IAnsiConsole console, IControlClient controlClient, CliVersion cliVersion)
    : AsyncCommand<ReloadCommand.Settings>
{
    public const string Name = "reload";

    public sealed class Settings : CliSettings
    {
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var call = await controlClient.SendAsync(ControlProtocol.ReloadCommand, cancellationToken);

        if (ControlCommandSupport.Unanswered(call, cliVersion) is { } failure)
            return CommandSupport.Fail(console, Name, settings.Json, failure.ExitCode, failure.Message);

        var response = call.Response!;
        switch (response.Kind)
        {
            case ControlResponseKind.Degraded:
                return CommandSupport.Fail(console, Name, settings.Json, ExitCodes.ResidentNotRunning,
                    $"取り込みが止まっているため設定リロードできません: {response.Error}");
            case ControlResponseKind.Ok when response.Reload is { } reload:
                var result = new ReloadJson(reload.Accepted, reload.Error);
                var exitCode = reload.Accepted ? ExitCodes.Success : ExitCodes.UsageOrConfig;
                if (settings.Json)
                    CliJson.Write(console, CliJson.Result(Name, exitCode, result));
                else if (reload.Accepted)
                    console.MarkupLine("[green]設定リロードを受理しました[/](反映は取り込みの合間に行います。失敗したら前の設定のまま動き続け、gui.log に残ります)");
                else
                    console.MarkupLineInterpolated($"[red]設定リロードは拒否されました:[/] {reload.Error}");
                return exitCode;
            default:
                return CommandSupport.Fail(console, Name, settings.Json, ExitCodes.UsageOrConfig,
                    response.Error ?? "常駐の応答に設定リロードの結果がありませんでした");
        }
    }
}
