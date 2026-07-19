using System.ComponentModel;
using Radirec.Core.Organization;
using Radirec.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Radirec.Cli.Commands;

/// <summary>
/// 散在するソース録音を <c>{to}/{正規放送局}/{番組}/…</c> 構造へ仕分ける(#81)。取込(ingest)とは別で、
/// タグ書換も Jellyfin もせず、ソースの物理再配置だけを行う。ファイル名は不変(取込互換のため)。
/// </summary>
public sealed class OrganizeSourceCommand : Command<OrganizeSourceCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-c|--config <PATH>")]
        [Description(@"設定ファイル(radirec.json)のパス。省略時は既定パス")]
        public string? Config { get; init; }

        [CommandOption("--from <PATH>")]
        [Description("整理元(散在ソース)のルート")]
        public string? From { get; init; }

        [CommandOption("--to <PATH>")]
        [Description("整理先ルート。省略時は radirec.json の RadioRoot")]
        public string? To { get; init; }

        [CommandOption("--dry-run")]
        [Description("副作用なしで仕分け予定を表示する")]
        public bool DryRun { get; init; }

        [CommandOption("--copy")]
        [Description("移動でなくコピー(移動元を残す)")]
        public bool Copy { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = ConfigLoading.Load(settings.Config);
        if (config is null)
            return 1;

        if (string.IsNullOrWhiteSpace(settings.From))
        {
            AnsiConsole.MarkupLine("[red]--from(整理元ルート)は必須です。[/]");
            return 1;
        }

        var to = string.IsNullOrWhiteSpace(settings.To) ? config.RadioRoot : settings.To;
        if (string.IsNullOrWhiteSpace(to))
        {
            AnsiConsole.MarkupLine("[red]--to か radirec.json の RadioRoot が必要です。[/]");
            return 1;
        }

        var organizer = CompositionRoot.BuildOrganizer(config, settings.From!, to);
        var report = organizer.Run(settings.DryRun, settings.Copy);
        Render(report);
        return 0;
    }

    private static void Render(OrganizeReport report)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("結果");
        table.AddColumn("移動元");
        table.AddColumn("移動先 / 詳細");

        foreach (var item in report.Items)
        {
            var (label, color) = Describe(item.Outcome);
            table.AddRow(
                new Markup($"[{color}]{label}[/]"),
                new Markup(Markup.Escape(Path.GetFileName(item.SourcePath))),
                new Markup(Markup.Escape(Detail(item))));
        }

        AnsiConsole.Write(table);

        // 集計は 1 パスで数える(件数分の LINQ 再走査を避ける)。
        var tally = new Dictionary<OrganizeOutcome, int>();
        foreach (var item in report.Items)
            tally[item.Outcome] = tally.GetValueOrDefault(item.Outcome) + 1;
        int N(OrganizeOutcome o) => tally.GetValueOrDefault(o);

        var verb = report.Copy ? "コピー" : "移動";
        var mode = report.DryRun ? "[cyan](dry-run)[/]" : string.Empty;
        AnsiConsole.MarkupLine(
            $"{verb} {N(OrganizeOutcome.Moved) + N(OrganizeOutcome.Copied)} / " +
            $"重複 {N(OrganizeOutcome.DuplicateSkipped)} / 衝突 {N(OrganizeOutcome.Conflict)} / " +
            $"既定位置 {N(OrganizeOutcome.AlreadyInPlace)} / ジャンク {N(OrganizeOutcome.SkippedJunk)} / " +
            $"未安定 {N(OrganizeOutcome.SkippedUnstable)} / 対象外 {N(OrganizeOutcome.Unsupported)} / " +
            $"失敗 {N(OrganizeOutcome.Failed)} {mode}");

        // 移動されず移動元に残り、人手確認が要る件(別内容衝突 + 失敗)を明示する。
        var needsAttention = N(OrganizeOutcome.Conflict) + N(OrganizeOutcome.Failed);
        if (needsAttention > 0)
            AnsiConsole.MarkupLine(
                $"[yellow]要確認: {needsAttention} 件が移動元に残っています(衝突/失敗)。[/]");
    }

    private static string Detail(OrganizeItemResult item) => item.Outcome switch
    {
        OrganizeOutcome.Failed => item.Message ?? string.Empty,
        _ => item.TargetPath ?? string.Empty,
    };

    private static (string Label, string Color) Describe(OrganizeOutcome outcome) => outcome switch
    {
        OrganizeOutcome.Moved => ("移動", "green"),
        OrganizeOutcome.Copied => ("コピー", "green"),
        OrganizeOutcome.DuplicateSkipped => ("重複", "grey"),
        OrganizeOutcome.Conflict => ("衝突", "red"),
        OrganizeOutcome.AlreadyInPlace => ("既定位置", "grey"),
        OrganizeOutcome.SkippedJunk => ("ジャンク", "grey"),
        OrganizeOutcome.SkippedUnstable => ("未安定", "yellow"),
        OrganizeOutcome.Unsupported => ("対象外", "grey"),
        OrganizeOutcome.Failed => ("失敗", "red"),
        _ => (outcome.ToString(), "white"),
    };
}
