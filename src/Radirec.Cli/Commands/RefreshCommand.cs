using System.ComponentModel;
using Radirec.Hosting;
using Radirec.Core.Jellyfin;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Commands;

/// <summary>Jellyfin のライブラリ更新を手動で起動する(疎通確認・単発更新用)。</summary>
public sealed class RefreshCommand : AsyncCommand<RefreshCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-c|--config <PATH>")]
        [Description("設定ファイル(radirec.json)のパス。省略時は " + ConfigPaths.DefaultConfigDisplay)]
        public string? Config { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = ConfigLoading.Load(settings.Config);
        if (config is null)
            return 1;

        var (refresher, disposable) = CompositionRoot.BuildRefresher(config);
        if (refresher is NullLibraryRefresher)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Jellyfin が未設定です。[/]radirec.json の JellyfinUrl / JellyfinApiKey を設定してください。");
            return 1;
        }

        using (disposable)
        {
            try
            {
                await refresher.RefreshAsync(cancellationToken);
                AnsiConsole.MarkupLine("[green]Jellyfin ライブラリ更新を起動しました。[/]");
                return 0;
            }
            // HTTP エラー(JellyfinException)に加え、接続不可/DNS/TLS(HttpRequestException)・
            // タイムアウト(TaskCanceledException)も親切に扱う(疎通確認コマンドの主目的)。
            catch (Exception ex) when (ex is JellyfinException or HttpRequestException or TaskCanceledException)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]ライブラリ更新に失敗しました:[/] {ex.Message}");
                return 1;
            }
        }
    }
}
