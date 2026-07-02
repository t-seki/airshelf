using System.ComponentModel;
using Radirec.Cli.Configuration;
using Radirec.Core.Ingestion;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Commands;

/// <summary>らじれこ録音を正規化して取り込む(1 回走査)。</summary>
public sealed class IngestCommand : AsyncCommand<IngestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-c|--config <PATH>")]
        [Description("設定ファイル(radirec.json)のパス")]
        [DefaultValue("radirec.json")]
        public string Config { get; init; } = "radirec.json";

        [CommandOption("--dry-run")]
        [Description("副作用なしで取り込み予定を表示する")]
        public bool DryRun { get; init; }
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
        IngestService service;
        IDisposable? disposable;
        try
        {
            config = ConfigLoader.LoadRequired<RadirecConfig>(settings.Config);
            (service, disposable) = CompositionRoot.Build(config, settings.DryRun);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]設定の読み込みに失敗しました:[/] {ex.Message}");
            return 1;
        }

        using (disposable)
        {
            var report = await service.RunOnceAsync(settings.DryRun, cancellationToken);
            Render(report);
        }

        return 0;
    }

    private static void Render(IngestReport report)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("結果");
        table.AddColumn("ファイル");
        table.AddColumn("出力 / 詳細");

        foreach (var item in report.Items)
        {
            var (label, color) = Describe(item.Outcome);
            var fallback = item.UsedFallback ? " [yellow](fallback)[/]" : string.Empty;
            table.AddRow(
                new Markup($"[{color}]{label}[/]{fallback}"),
                new Markup(Markup.Escape(Path.GetFileName(item.SourcePath))),
                new Markup(Markup.Escape(Detail(item))));
        }

        AnsiConsole.Write(table);

        var mode = report.DryRun ? "[cyan](dry-run)[/]" : string.Empty;
        AnsiConsole.MarkupLine(
            $"取込 {report.Count(IngestOutcome.Imported)} / 予定 {report.Count(IngestOutcome.Planned)} / " +
            $"済 {report.Count(IngestOutcome.SkippedAlreadyImported)} / 未安定 {report.Count(IngestOutcome.SkippedUnstable)} / " +
            $"ジャンク {report.Count(IngestOutcome.SkippedJunk)} / 対象外 {report.Count(IngestOutcome.SkippedUnsupported)} / " +
            $"失敗 {report.Count(IngestOutcome.Failed)} {mode}");
    }

    private static string Detail(IngestItemResult item) => item.Outcome switch
    {
        IngestOutcome.Imported or IngestOutcome.Planned => item.OutputPath ?? string.Empty,
        IngestOutcome.Failed => item.Message ?? string.Empty,
        _ => string.Empty,
    };

    private static (string Label, string Color) Describe(IngestOutcome outcome) => outcome switch
    {
        IngestOutcome.Imported => ("取込", "green"),
        IngestOutcome.Planned => ("予定", "cyan"),
        IngestOutcome.SkippedAlreadyImported => ("済", "grey"),
        IngestOutcome.SkippedUnstable => ("未安定", "yellow"),
        IngestOutcome.SkippedJunk => ("ジャンク", "grey"),
        IngestOutcome.SkippedUnsupported => ("対象外", "grey"),
        IngestOutcome.Failed => ("失敗", "red"),
        _ => (outcome.ToString(), "white"),
    };
}
