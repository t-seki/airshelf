using System.ComponentModel;
using Radirec.Core.Ingestion;
using Radirec.Core.Reapply;
using Radirec.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Commands;

/// <summary>
/// 今の番組の設定(programs.json の正規名・統合)を取り込み済みの回へ再適用する(ADR-0016)。
/// まず <c>--dry-run</c> で、どのアルバムの何回が動くかを確かめる。
/// </summary>
public sealed class ReapplyCommand : AsyncCommand<ReapplyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-c|--config <PATH>")]
        [Description("設定ファイル(radirec.json)のパス。省略時は " + ConfigPaths.DefaultConfigDisplay)]
        public string? Config { get; init; }

        [CommandOption("--dry-run")]
        [Description("副作用なしで再適用の予定を表示する")]
        public bool DryRun { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = ConfigLoading.Load(settings.Config);
        if (config is null)
            return 1;

        ReapplyService service;
        IDisposable? disposable;
        try
        {
            (service, disposable) = CompositionRoot.BuildReapply(config, settings.DryRun);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            // 台帳オープンや stations/programs の読み込みの失敗(radirec.json 本体は ConfigLoading.Load で処理済み)。
            AnsiConsole.MarkupLineInterpolated($"[red]設定の読み込みに失敗しました:[/] {ex.Message}");
            return 1;
        }

        using (disposable)
        {
            var report = await service.RunAsync(settings.DryRun, cancellationToken);
            Render(report, config.OutputRoot);
        }

        return 0;
    }

    private static void Render(ReapplyReport report, string outputRoot)
    {
        var mode = report.DryRun ? " [cyan](dry-run)[/]" : string.Empty;

        // 変える回が無ければ「揃っています」(トレイ #126 も同じ判定)。対象外の回だけが残っていれば一覧を添える。
        if (report.IsAligned)
        {
            AnsiConsole.MarkupLine($"取り込み済みの回は今の設定と揃っています{mode}");
            if (report.Items.Count > 0)
            {
                AnsiConsole.MarkupLine($"ただし、次の {report.Items.Count} 回は対象外のため動かしません:");
                RenderItems(report, outputRoot);
            }
        }
        else
        {
            RenderAlbums(report);
            AnsiConsole.MarkupLineInterpolated($"[grey]明細のパスは出力先({outputRoot})からの相対パスです[/]");
            RenderItems(report, outputRoot);
            AnsiConsole.MarkupLine(
                $"移動・リネーム {report.Count(ReapplyAction.Move)} / 台帳だけ直す {report.Count(ReapplyAction.LedgerOnly)} / " +
                $"重複が残っている {report.Count(ReapplyAction.DuplicateLeft)} / 対象外 {report.Count(ReapplyAction.Excluded)} / " +
                $"失敗 {report.Count(ReapplyAction.Failed)}{mode}");
        }

        RenderSiblings(report);

        switch (report.LibraryRefresh)
        {
            case LibraryRefreshStatus.Started:
                AnsiConsole.MarkupLine("[green]Jellyfin ライブラリ更新: 起動しました[/]");
                break;
            case LibraryRefreshStatus.NotConfigured:
                AnsiConsole.MarkupLine("[grey]Jellyfin ライブラリ更新: 未設定のため行いません[/]");
                break;
            case LibraryRefreshStatus.Failed:
                AnsiConsole.MarkupLineInterpolated($"[yellow]Jellyfin ライブラリ更新に失敗:[/] {report.RefreshError}");
                break;
        }

        // 移動した回のタグは通常スキャンで反映されないことがある(ADR-0016。FullRefresh は自動化しない)。
        if (!report.DryRun && report.Count(ReapplyAction.Move) > 0)
            AnsiConsole.MarkupLine(
                "[grey]タグ(title / track)が反映されないアルバムがあれば、Jellyfin でメタデータを更新してください。[/]");
    }

    private static void RenderAlbums(ReapplyReport report)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("アルバム単位のまとめ");
        table.AddColumn("放送局");
        table.AddColumn("変更前 → 変更後");
        table.AddColumn(new TableColumn("移動").RightAligned());
        table.AddColumn(new TableColumn("台帳のみ").RightAligned());
        table.AddColumn(new TableColumn("重複").RightAligned());
        table.AddColumn(new TableColumn("対象外").RightAligned());
        table.AddColumn(new TableColumn("失敗").RightAligned());

        foreach (var album in report.Albums)
        {
            table.AddRow(
                Markup.Escape(album.Station),
                Markup.Escape($"{album.FromAlbum} → {album.ToAlbum}"),
                album.Moves.ToString(),
                album.LedgerOnly.ToString(),
                album.DuplicatesLeft.ToString(),
                album.Excluded.ToString(),
                album.Failed.ToString());
        }

        AnsiConsole.Write(table);
    }

    private static void RenderItems(ReapplyReport report, string outputRoot)
    {
        // 出力先の下なら相対パスにして短くする(出力先の外 = 台帳の記録が古い等はそのまま出す)。
        string Short(string path)
        {
            var relative = Path.GetRelativePath(outputRoot, path);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
        }

        var table = new Table().Border(TableBorder.Rounded).Title("明細");
        table.AddColumn("結果");
        table.AddColumn("放送日時");
        table.AddColumn("変更前");
        table.AddColumn("変更後 / 詳細");

        foreach (var item in report.Items)
        {
            var (label, color) = Describe(item.Action, report.DryRun);
            var when = item.BroadcastTime is { } t
                ? $"{item.BroadcastDate:yyyy-MM-dd} {t:HH\\:mm}"
                : item.BroadcastDate.ToString("yyyy-MM-dd");
            var detail = item.Reason is { } reason ? $"{Short(item.ToPath)}({reason})" : Short(item.ToPath);
            table.AddRow(
                new Markup($"[{color}]{label}[/]"),
                new Markup(Markup.Escape(when)),
                new Markup(Markup.Escape(Short(item.FromPath))),
                new Markup(Markup.Escape(detail)));
        }

        AnsiConsole.Write(table);
    }

    private static void RenderSiblings(ReapplyReport report)
    {
        if (report.UnintegratedSiblings.Count == 0)
            return;

        AnsiConsole.MarkupLine("[yellow]統合設定の無い兄弟フォルダ(programs.json で統合するか確かめてください):[/]");
        foreach (var sibling in report.UnintegratedSiblings)
        {
            var folders = string.Join(" / ", sibling.Group.Folders.Select(f =>
                sibling.FoldersWithoutSetting.Contains(f) ? $"{f}(設定なし)" : f));
            AnsiConsole.MarkupLineInterpolated($"  {sibling.Group.Station}: {folders}");
        }
    }

    private static (string Label, string Color) Describe(ReapplyAction action, bool dryRun) => action switch
    {
        ReapplyAction.Move => (dryRun ? "移動予定" : "移動", dryRun ? "cyan" : "green"),
        ReapplyAction.LedgerOnly => ("台帳のみ", "green"),
        ReapplyAction.DuplicateLeft => ("重複", "yellow"),
        ReapplyAction.Excluded => ("対象外", "grey"),
        ReapplyAction.Failed => ("失敗", "red"),
        _ => (action.ToString(), "white"),
    };
}
