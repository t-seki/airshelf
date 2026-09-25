using Radirec.Cli.Core.Output;
using Radirec.Core.Jellyfin;
using Radirec.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Core.Commands;

/// <summary>Jellyfin のライブラリ更新を手動で起動する(疎通確認・単発更新用)。</summary>
public sealed class RefreshCommand(IAnsiConsole console, ICliConfigSource configSource)
    : AsyncCommand<RefreshCommand.Settings>
{
    public const string Name = "refresh";

    public sealed class Settings : CliSettings
    {
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = CommandSupport.LoadConfig(configSource, console, Name, settings.Json);
        if (config is null)
            return ExitCodes.UsageOrConfig;

        var (refresher, disposable) = CompositionRoot.BuildRefresher(config);
        RefreshJson result;
        using (disposable)
            result = await RunAsync(refresher, cancellationToken);

        var exitCode = ExitCodeRules.For(result.Status);
        if (settings.Json)
            CliJson.Write(console, CliJson.Result(Name, exitCode, result));
        else
            Render(result);
        return exitCode;
    }

    /// <summary>
    /// 更新を起動して結果を返す(表示はしない。テストはここを確かめる)。HTTP エラー(<see cref="JellyfinException"/>)に加え、
    /// 接続不可・DNS・TLS(<see cref="HttpRequestException"/>)とタイムアウト(<see cref="TaskCanceledException"/>)も
    /// 結果にする(疎通確認コマンドの主目的)。JellyfinUrl の形式不正は、繋ぎに行かずに設定の不正として返す。
    /// </summary>
    public static async Task<RefreshJson> RunAsync(ILibraryRefresher refresher, CancellationToken cancellationToken)
    {
        if (refresher is NullLibraryRefresher)
            return new RefreshJson(RefreshStatus.NotConfigured,
                "Jellyfin が未設定です。radirec.json の JellyfinUrl / JellyfinApiKey を設定してください。");

        try
        {
            await refresher.RefreshAsync(cancellationToken);
            return new RefreshJson(RefreshStatus.Started, null);
        }
        catch (JellyfinException ex) when (refresher is MisconfiguredLibraryRefresher)
        {
            return new RefreshJson(RefreshStatus.Misconfigured, ex.Message);
        }
        catch (Exception ex) when (ex is JellyfinException or HttpRequestException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new RefreshJson(RefreshStatus.Failed, ex.Message);
        }
    }

    private void Render(RefreshJson result)
    {
        switch (result.Status)
        {
            case RefreshStatus.Started:
                console.MarkupLine("[green]Jellyfin ライブラリ更新を起動しました。[/]");
                break;
            case RefreshStatus.NotConfigured:
                console.MarkupLineInterpolated($"[yellow]{result.Error}[/]");
                break;
            default:
                console.MarkupLineInterpolated($"[red]ライブラリ更新に失敗しました:[/] {result.Error}");
                break;
        }
    }
}
