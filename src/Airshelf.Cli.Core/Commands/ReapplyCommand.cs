using System.ComponentModel;
using Airshelf.Cli.Core.Output;
using Airshelf.Core.Ingestion;
using Airshelf.Core.Reapply;
using Airshelf.Hosting.Control;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Airshelf.Cli.Core.Commands;

/// <summary>
/// 今の番組の設定(programs.json の正規名・統合)を取り込み済みの回へ再適用する(ADR-0016)。台帳と出力先を書き換えるので、CLI が
/// 自分では走らせず、制御チャネルで常駐に頼む(ADR-0020 / #181)。常駐が動いていなければ 2 で終わる。
/// <list type="bullet">
/// <item>オプションなし(<c>--dry-run</c> も同じ):確認だけを行い、予定と予定 ID(CONTEXT.md)を出す。常駐の設定リロードはしない。</item>
/// <item><c>--apply &lt;予定 ID&gt;</c>:常駐が設定リロード → dry-run し直し → 予定 ID が一致したときだけ実行する。一致しなければ実行せず、
/// 新しい予定と予定 ID を出して 3 で終わる。</item>
/// </list>
/// 再適用の準備で台帳のある場所(NAS など)に届かなければ、確認・実行とも 5 で終わる(#223)。
/// </summary>
public sealed class ReapplyCommand(
    IAnsiConsole console, ICliConfigSource configSource, IControlClient controlClient, CliVersion cliVersion)
    : AsyncCommand<ReapplyCommand.Settings>
{
    public const string Name = "reapply";
    public sealed class Settings : CliSettings
    {
        [CommandOption("--dry-run")]
        [Description("副作用なしで再適用の予定と予定 ID を表示する(オプションなしと同じ)")]
        public bool DryRun { get; init; }
        [CommandOption("--apply <PLAN_ID>")]
        [Description("dry-run が出した予定 ID の予定を実行する(予定が変わっていれば実行せず、終了コード 3)")]
        public string? Apply { get; init; }
    }
    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.DryRun && settings.Apply is not null)
            return CommandSupport.Fail(
                console, Name, settings.Json, ExitCodes.UsageOrConfig, "--dry-run と --apply は一緒に指定できません");
        if (settings.Apply is { } id && !ReapplyPlanId.IsWellFormed(id))
            return CommandSupport.Fail(
                console, Name, settings.Json, ExitCodes.UsageOrConfig,
                $"予定 ID の形が違います({ReapplyPlanId.Length} 文字の 16 進。reapply の dry-run が出した値): {id}");
        var planId = settings.Apply is { } apply ? ReapplyPlanId.Normalize(apply) : null;
        var call = await controlClient.SendAsync(
            new ControlRequest(ControlProtocol.Version, ControlProtocol.ReapplyCommand, planId), cancellationToken);
        if (ControlCommandSupport.Unanswered(call, cliVersion) is { } unanswered)
            return CommandSupport.Fail(console, Name, settings.Json, unanswered.ExitCode, unanswered.Message);
        var response = call.Response!;
        if (response.Kind == ControlResponseKind.Degraded)
            return CommandSupport.Fail(console, Name, settings.Json, ExitCodes.ResidentNotRunning,
                $"取り込みが止まっているため再適用できません: {response.Error}");
        if (response is not { Kind: ControlResponseKind.Ok, Reapply: { } result })
            // 知らないコマンド(再適用に対応していない古いトレイ)など。
            return CommandSupport.Fail(console, Name, settings.Json, ExitCodes.UsageOrConfig,
                response.Error is { } error
                    ? $"常駐が再適用を受け付けませんでした: {error}(トレイが古ければ更新してください)"
                    : "常駐の応答に再適用の結果がありませんでした");
        switch (result)
        {
            // 予定 ID が合わなかった:実行していない。新しい予定と予定 ID を、理由と一緒に出す。
            case { IsStale: true, Report: { } replanned }:
                Output(settings.Json, ExitCodes.PlanChanged, replanned, result.PlanId, result.Error);
                return ExitCodes.PlanChanged;
            case { Report: { } report }:
            {
                var exitCode = ExitCodeRules.For(report);
                Output(settings.Json, exitCode, report, result.PlanId ?? planId, error: null);
                return exitCode;
            }
            case { IsStopped: true }:
                return CommandSupport.Fail(console, Name, settings.Json, ExitCodes.ResidentNotRunning,
                    result.Error ?? "取り込みが止まっているため再適用できません");
            // 再適用の準備で、台帳のある場所(NAS など)に届かなかった(#223)。ほかのコマンドと同じく 5。
            case { IsUnreachable: true }:
                return CommandSupport.Fail(console, Name, settings.Json, ExitCodes.ExternalFailure,
                    result.Error ?? "台帳のある場所に届かないため再適用できません");
            default:
                // 設定を読めない・反映できない・再適用の準備(台帳を開く等)に失敗した。
                return CommandSupport.Fail(console, Name, settings.Json, ExitCodes.UsageOrConfig,
                    result.Error ?? "再適用に失敗しました");
        }
    }
    private void Output(bool json, int exitCode, ReapplyReport report, string? planId, string? error)
    {
        if (json)
        {
            // 予定が変わった(3)ときは、結果(新しい予定)と一緒に理由も error に入れる。
            CliJson.Write(console, CliJson.Result(Name, exitCode, ReapplyJson.From(report, planId)) with { Error = error });
            return;
        }
        if (error is not null)
            console.MarkupLineInterpolated($"[yellow]{error}[/]");
        // 表示を短くするため、出力先からの相対パスにする。設定を読めなければ(常駐とは別に CLI が読むだけ)そのまま出す。
        var outputRoot = configSource.TryLoad(out var config, out _) && config is not null ? config.OutputRoot : null;
        Render(report, outputRoot);
        // 予定 ID は写して使う値なので、装飾しない(色・太字の制御文字が値の途中に入らないように)。
        if (report.DryRun && planId is not null)
        {
            console.MarkupLineInterpolated($"予定 ID: {planId}");
            console.MarkupLineInterpolated($"実行するには: airshelf reapply --apply {planId}");
        }
    }
    private void Render(ReapplyReport report, string? outputRoot)
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
            if (outputRoot is not null)
                console.MarkupLineInterpolated($"[grey]明細のパスは出力先({outputRoot})からの相対パスです[/]");
            RenderItems(report, outputRoot);
            // 出力先から消す回は、あるときだけ出す(差し替え放送の判断が無い利用者の表示を変えないため。トレイの確認と同じ)。
            var removals = report.Count(ReapplyAction.Remove) is var r and > 0 ? $"出力先から消す {r} / " : string.Empty;
            console.MarkupLine(
                $"移動・リネーム {report.Count(ReapplyAction.Move)} / 台帳とタグを直す {report.Count(ReapplyAction.LedgerAndTags)} / " +
                removals +
                $"台帳だけ直す {report.Count(ReapplyAction.LedgerOnly)} / " +
                $"重複が残っている {report.Count(ReapplyAction.DuplicateLeft)} / 対象外 {report.Count(ReapplyAction.Excluded)} / " +
                $"失敗 {report.Count(ReapplyAction.Failed)}{mode}");
            if (report.DuplicatesRewritingTags > 0)
                console.MarkupLine(
                    $"[grey]重複が残っている回のうち {report.DuplicatesRewritingTags} 回は、移動先のファイルのタグ(album / title / track)も" +
                    (report.DryRun ? "書き換えます。[/]" : "書き換えました。[/]"));
            if (report.Count(ReapplyAction.Remove) > 0)
                console.MarkupLine(report.DryRun
                    ? "[grey]出力先から消す回は、ファイル名の番組名に「取り込まない」と判断した回です。出力先のファイルと台帳の記録を消します" +
                      "(取込元の録音は消しません。判断を戻せば取り込み直されます)。[/]"
                    : "[grey]出力先から消した回の録音は、取込元に残っています(判断を戻すと、次の取り込みで取り込み直されます)。[/]");
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
            case LibraryRefreshStatus.Misconfigured:
                console.MarkupLineInterpolated($"[yellow]Jellyfin ライブラリ更新を行えません(設定の誤り):[/] {report.RefreshError}");
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
        var hasRemovals = report.Count(ReapplyAction.Remove) > 0;
        if (hasRemovals)
            table.AddColumn(new TableColumn("消す").RightAligned());
        table.AddColumn(new TableColumn("台帳のみ").RightAligned());
        table.AddColumn(new TableColumn("重複").RightAligned());
        table.AddColumn(new TableColumn("対象外").RightAligned());
        table.AddColumn(new TableColumn("失敗").RightAligned());

        foreach (var album in report.Albums)
        {
            var cells = new List<string>
            {
                Markup.Escape(album.Station),
                Markup.Escape($"{album.FromAlbum} → {album.ToAlbum}"),
                album.Moves.ToString(),
                album.LedgerAndTags.ToString(),
            };
            if (hasRemovals)
                cells.Add(album.Removals.ToString());
            cells.AddRange(
            [
                album.LedgerOnly.ToString(),
                album.DuplicatesLeft.ToString(),
                album.Excluded.ToString(),
                album.Failed.ToString(),
            ]);
            table.AddRow(cells.ToArray());
        }

        console.Write(table);
    }

    private void RenderItems(ReapplyReport report, string? outputRoot)
    {
        // 出力先の下なら相対パスにして短くする(出力先の外 = 台帳の記録が古い等はそのまま出す)。
        string Short(string path)
        {
            if (outputRoot is null)
                return path;
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
        ReapplyAction.Remove => (dryRun ? "消す予定" : "消した", dryRun ? "cyan" : "green"),
        _ => (action.ToString(), "white"),
    };
}
