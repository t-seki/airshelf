using System.ComponentModel;
using Airshelf.Cli.Core.Output;
using Airshelf.Core.Configuration;
using Airshelf.Core.Ingestion;
using Airshelf.Hosting;
using Airshelf.Hosting.Control;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Airshelf.Cli.Core.Commands;

/// <summary>
/// らじれこ録音を正規化して取り込む(1 回走査)。台帳と出力先を書き換えるので、CLI が自分では走査せず、制御チャネルで常駐に
/// 走査の実行を頼み、終わるまで待って結果を出す(ADR-0020 / #180)。常駐が動いていなければ 2 で終わる(CLI が代わりに走査しない)。
/// <c>--dry-run</c> は台帳を読むだけなので、今までどおり CLI が直接行う(常駐が動いていなくても使える)。
/// </summary>
public sealed class IngestCommand(
    IAnsiConsole console, ICliConfigSource configSource, IControlClient controlClient, CliVersion cliVersion)
    : AsyncCommand<IngestCommand.Settings>
{
    public const string Name = "ingest";

    public sealed class Settings : CliSettings
    {
        [CommandOption("--dry-run")]
        [Description("副作用なしで取り込み予定を表示する")]
        public bool DryRun { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ScanRunReport report;
        if (settings.DryRun)
        {
            if (await DryRunAsync(settings, cancellationToken) is not { } planned)
                return ExitCodes.UsageOrConfig;
            report = planned;
        }
        else
        {
            var (ran, failure) = await RunOnResidentAsync(cancellationToken);
            if (failure is { } f)
                return CommandSupport.Fail(console, Name, settings.Json, f.ExitCode, f.Message);
            report = ran!;
        }

        var exitCode = ExitCodeRules.For(report);
        if (settings.Json)
            CliJson.Write(console, CliJson.Result(Name, exitCode, IngestJson.From(report)));
        else
            Render(report);
        return exitCode;
    }

    /// <summary>取り込み予定を CLI が直接計算する(台帳は読み取り専用)。設定を読めなければ理由を出して null。</summary>
    private async Task<ScanRunReport?> DryRunAsync(Settings settings, CancellationToken cancellationToken)
    {
        var config = CommandSupport.LoadConfig(configSource, console, Name, settings.Json);
        if (config is null)
            return null;

        IngestService service;
        IDisposable? disposable;
        try
        {
            (service, disposable) = CompositionRoot.Build(config, dryRun: true);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ProgramsConfigException
                                   or IOException and not LedgerUnreachableException)
        {
            // 台帳オープンや stations/programs の読み込みで失敗した場合、programs.json の中身が使えない場合
            // (「別の番組として取り込む」にアルバム名が無い等。ADR-0021)。airshelf.json 本体の解析失敗は LoadConfig 側で処理済み。台帳のフォルダに届かないときは外へ出して、ほかのコマンドと同じく
            // 5 にする(AirshelfCli.HandleException。#216)。
            CommandSupport.Fail(
                console, Name, settings.Json, ExitCodes.UsageOrConfig, $"設定の読み込みに失敗しました: {ex.Message}");
            return null;
        }

        using (disposable)
            return ScanRunReport.From(await service.RunOnceAsync(dryRun: true, cancellationToken));
    }

    /// <summary>常駐に走査の実行を頼み、終わるまで待つ。結果が得られなければ終了コードと理由を返す。</summary>
    private async Task<(ScanRunReport? Report, (int ExitCode, string Message)? Failure)> RunOnResidentAsync(
        CancellationToken cancellationToken)
    {
        var call = await controlClient.SendAsync(ControlProtocol.ScanCommand, cancellationToken);
        if (ControlCommandSupport.Unanswered(call, cliVersion) is { } unanswered)
            return (null, unanswered);

        var response = call.Response!;
        return response.Kind switch
        {
            ControlResponseKind.Degraded => (null,
                (ExitCodes.ResidentNotRunning, $"取り込みが止まっているため走査できません: {response.Error}")),
            ControlResponseKind.Ok when response.ScanRun is { Report: { } report } => (report, null),
            ControlResponseKind.Ok when response.ScanRun is { } failed => (null,
                (ExitCodeOf(failed.Failure), failed.Error ?? "走査に失敗しました")),
            // 知らないコマンド(走査の実行に対応していない古いトレイ)など。
            _ => (null, (ExitCodes.UsageOrConfig,
                response.Error is { } error
                    ? $"常駐が走査の実行を受け付けませんでした: {error}(トレイが古ければ更新してください)"
                    : "常駐の応答に走査の結果がありませんでした")),
        };
    }

    /// <summary>
    /// 走査の実行が結果を返せなかったときの終了コード。取り込みが止まった → 2、取込元・出力先・Jellyfin に届かない → 5、それ以外 → 1
    /// (CLI が自分で走査していたときの、例外の終了コードの決め方と同じ)。
    /// </summary>
    private static int ExitCodeOf(ScanRunFailure? failure) => failure switch
    {
        ScanRunFailure.Stopped => ExitCodes.ResidentNotRunning,
        ScanRunFailure.External => ExitCodes.ExternalFailure,
        _ => ExitCodes.UsageOrConfig,
    };

    private void Render(ScanRunReport report)
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

        console.Write(table);

        var mode = report.DryRun ? "[cyan](dry-run)[/]" : string.Empty;
        console.MarkupLine(
            $"取込 {report.Count(IngestOutcome.Imported)} / 予定 {report.Count(IngestOutcome.Planned)} / " +
            $"済 {report.Count(IngestOutcome.SkippedAlreadyImported)} / 未安定 {report.Count(IngestOutcome.SkippedUnstable)} / " +
            $"ジャンク {report.Count(IngestOutcome.SkippedJunk)} / 対象外 {report.Count(IngestOutcome.SkippedUnsupported)} / " +
            $"失敗 {report.Count(IngestOutcome.Failed)} / 保留 {report.Count(IngestOutcome.Held)} / " +
            $"取り込まない {report.Count(IngestOutcome.SkippedByDecision)} {mode}");

        // 保留は失敗ではない(終了コードは変えない)が、判断するまで取り込まれないので、判断のしかたを添える(ADR-0021)。
        if (report.Count(IngestOutcome.Held) > 0)
            console.MarkupLine(
                $"[yellow]差し替え放送の疑いがある回を{(report.DryRun ? "保留します" : "保留しました")}。[/]" +
                "[grey]programs.json の FilenamePrograms に判断を書いて airshelf reload で反映すると、次の走査で判断どおりに扱います" +
                "(Match.Mode を Prefix・Contains にすると、スポンサー名やゲスト名の違う組を 1 つの判断で扱えます。" +
                "airshelf attention にも出ます)[/]");

        // 不要(取込0件・dry-run)のときは何も出さない。
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
    }

    private static string Detail(ScanRunItem item) => item.Outcome switch
    {
        // 取込/予定の警告(出力先の同名衝突を別名で避けた等。ADR-0015)は出力パスに添える。
        IngestOutcome.Imported or IngestOutcome.Planned => item.Message is { } warning
            ? $"{item.OutputPath}(警告: {warning})"
            : item.OutputPath ?? string.Empty,
        // 出力先に同じ内容が既にあって書かなかった回は、その既存ファイルを示す(台帳ヒットの回は null)。
        IngestOutcome.SkippedAlreadyImported => item.OutputPath ?? string.Empty,
        IngestOutcome.Failed => item.Message ?? string.Empty,
        // 保留・取り込まない判断は、判断の照合に使うファイル名の番組名を示す(ADR-0021)。
        IngestOutcome.Held or IngestOutcome.SkippedByDecision => item.FilenameProgram is { } name
            ? $"ファイル名の番組名: {name}"
            : string.Empty,
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
        IngestOutcome.Held => ("保留", "yellow"),
        IngestOutcome.SkippedByDecision => ("取り込まない", "grey"),
        _ => (outcome.ToString(), "white"),
    };
}
