using System.ComponentModel;
using Radirec.Cli.Configuration;
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
        [Description("設定ファイル(radirec.json)のパス")]
        [DefaultValue("radirec.json")]
        public string Config { get; init; } = "radirec.json";
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!File.Exists(settings.Config))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]設定ファイルが見つかりません:[/] {settings.Config}");
            return 1;
        }

        RadirecConfig config;
        try
        {
            config = ConfigLoader.LoadRequired<RadirecConfig>(settings.Config);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]設定の読み込みに失敗しました:[/] {ex.Message}");
            return 1;
        }

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
            catch (JellyfinException ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]ライブラリ更新に失敗しました:[/] {ex.Message}");
                return 1;
            }
        }
    }
}
