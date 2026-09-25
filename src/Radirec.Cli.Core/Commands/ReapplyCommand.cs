using System.ComponentModel;
using Radirec.Cli.Core.Output;
using Radirec.Core.Ingestion;
using Radirec.Core.Reapply;
using Radirec.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Core.Commands;

/// <summary>
/// 今の番組の設定(programs.json の正規名・統合)を取り込み済みの回へ再適用する(ADR-0016)。
/// まず <c>--dry-run</c> で、どのアルバムの何回が動くかを確かめる。
/// </summary>
public sealed class ReapplyCommand(IAnsiConsole console, ICliConfigSource configSource)
    : AsyncCommand<ReapplyCommand.Settings>
{
    public const string Name = "reapply";

    public sealed class Settings : CliSettings
    {
        [CommandOption("--dry-run")]
        [Description("副作用なしで再適用の予定を表示する")]
        public bool DryRun { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = CommandSupport.LoadConfig(configSource, console, Name, settings.Json);
        if (config is null)
            return ExitCodes.UsageOrConfig;

        ReapplyService service;
        IDisposable? disposable;
        try
        {
            (service, disposable) = CompositionRoot.BuildReapply(config, settings.DryRun);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            // 台帳オープンや stations/programs の読み込みの失敗(radirec.json 本体は LoadConfig で処理済み)。
            return CommandSupport.Fail(
                console, Name, settings.Json, ExitCodes.UsageOrConfig, $"設定の読み込みに失敗しました: {ex.Message}");
        }

        ReapplyReport report;
        using (disposable)
            report = await service.RunAsync(settings.DryRun, cancellationToken);

        var exitCode = ExitCodeRules.For(report);
        if (settings.Json)
            CliJson.Write(console, CliJson.Result(Name, exitCode, ReapplyJson.From(report)));
        else
            Render(report, config.OutputRoot);
        return exitCode;
    }

    private void Render(ReapplyReport report, string outputRoot)
    {
        var mode = report.DryRun ? " [cyan](dry-run)[/]" : string.Empty;

        // 変える回が無ければ「揃っています」(トレイ #126 も同じ判定)。対象外の回だけが残っていれば一覧を添える。
        if (report.IsAligned)
        {
            console.MarkupLine($"取り込み済みの回は今の設定と揃っています{mode}");
            if (report.Items.Count > 0)
            {
                console.MarkupLine($"ただし、次の {report.Items.Count} 回は対象外のため動かしません:");
                RenderItems(report, outputRoot);
            }
        }
        else
        {
            RenderAlbums(report);
            console.MarkupLineInterpolated($"[grey]明細のパスは出力先({outputRoot})からの相対パスです[/]");
            RenderItems(report, outputRoot);
            console.MarkupLine(
                $"移動・リネーム {report.Count(ReapplyAction.Move)} / 台帳とタグを直す {report.Count(ReapplyAction.LedgerAndTags)} / " +
                $"台帳だけ直す {report.Count(ReapplyAction.LedgerOnly)} / " +
                $"重複が残っている {report.Count(ReapplyAction.DuplicateLeft)} / 対象外 {report.Count(ReapplyAction.Excluded)} / " +
                $"失敗 {report.Count(ReapplyAction.Failed)}{mode}");
            if (report.DuplicatesRewritingTags > 0)
                console.MarkupLine(
                    $"[grey]重複が残っている回のうち {report.DuplicatesRewritingTags} 回は、移動先のファイルのタグ(album / title / track)も" +
                    (report.DryRun ? "書き換えます。[/]" : "書き換えました。[/]"));
        }

        RenderSiblings(report);

        switch (report.LibraryRefresh)
        {
            case LibraryRefreshStatus.Started:
                console.MarkupLine("[green]Jellyfin ライブラリ更新: 起動しました[/]");
                break;
            case LibraryRefreshStatus.NotConfigured:
                console.MarkupLine("[grey]Jellyfin ライブラリ更新: 未設定のため行いません[/]");
                break;
            case LibraryRefreshStatus.Failed:
                console.MarkupLineInterpolated($"[yellow]Jellyfin ライブラリ更新に失敗:[/] {report.RefreshError}");
                break;
        }

        // 書き換えた回のタグは通常スキャンで反映されないことがある(ADR-0016。FullRefresh は自動化しない)。
        if (!report.DryRun && report.RewritesFiles > 0)
            console.MarkupLine(
                "[grey]タグ(album / title / track)が反映されないアルバムがあれば、Jellyfin でメタデータを更新してください。[/]");
    }

    private void RenderAlbums(ReapplyReport report)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("アルバム単位のまとめ");
        table.AddColumn("放送局");
        table.AddColumn("変更前 → 変更後");
        table.AddColumn(new TableColumn("移動").RightAligned());
        table.AddColumn(new TableColumn("台帳とタグ").RightAligned());
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
                album.LedgerAndTags.ToString(),
                album.LedgerOnly.ToString(),
                album.DuplicatesLeft.ToString(),
                album.Excluded.ToString(),
                album.Failed.ToString());
        }

        console.Write(table);
    }

    private void RenderItems(ReapplyReport report, string outputRoot)
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

        console.Write(table);
    }

    private void RenderSiblings(ReapplyReport report)
    {
        if (report.UnintegratedSiblings.Count == 0)
            return;

        console.MarkupLine("[yellow]統合設定の無い兄弟フォルダ(programs.json で統合するか確かめてください):[/]");
        foreach (var sibling in report.UnintegratedSiblings)
        {
            var folders = string.Join(" / ", sibling.Group.Folders.Select(f =>
                sibling.FoldersWithoutSetting.Contains(f) ? $"{f}(設定なし)" : f));
            console.MarkupLineInterpolated($"  {sibling.Group.Station}: {folders}");
        }
    }

    private static (string Label, string Color) Describe(ReapplyAction action, bool dryRun) => action switch
    {
        ReapplyAction.Move => (dryRun ? "移動予定" : "移動", dryRun ? "cyan" : "green"),
        ReapplyAction.LedgerAndTags => ("台帳とタグ", dryRun ? "cyan" : "green"),
        ReapplyAction.LedgerOnly => ("台帳のみ", "green"),
        // 移動先のタグも直す重複の回は、明細の詳細(理由)にその旨が出る。
        ReapplyAction.DuplicateLeft => ("重複", "yellow"),
        ReapplyAction.Excluded => ("対象外", "grey"),
        ReapplyAction.Failed => ("失敗", "red"),
        _ => (action.ToString(), "white"),
    };
}
