using Radirec.Core.Ingestion;
using Radirec.Hosting.Control;

namespace Radirec.Cli.Core.Output;

/// <summary>
/// <c>ingest --json</c> の <c>result</c>。常駐に頼んだ走査の実行(#180)の結果も、<c>--dry-run</c> で CLI が直接計算した予定も同じ形。
/// </summary>
public sealed record IngestJson(
    bool DryRun,
    IngestCountsJson Counts,
    IReadOnlyList<IngestItemJson> Items,
    LibraryRefreshJson LibraryRefresh)
{
    public static IngestJson From(IngestReport report) => From(ScanRunReport.From(report));

    public static IngestJson From(ScanRunReport report) => new(
        report.DryRun,
        new IngestCountsJson(
            report.Count(IngestOutcome.Imported),
            report.Count(IngestOutcome.Planned),
            report.Count(IngestOutcome.SkippedAlreadyImported),
            report.Count(IngestOutcome.SkippedUnstable),
            report.Count(IngestOutcome.SkippedJunk),
            report.Count(IngestOutcome.SkippedUnsupported),
            report.Count(IngestOutcome.Failed),
            report.Count(IngestOutcome.Held),
            report.Count(IngestOutcome.SkippedByDecision)),
        report.Items
            .Select(i => new IngestItemJson(i.Outcome, i.SourcePath, i.OutputPath, i.Message, i.UsedFallback, i.FilenameProgram))
            .ToList(),
        new LibraryRefreshJson(report.LibraryRefresh, report.RefreshError));
}

/// <summary>結果ごとの件数(表の下の 1 行と同じ)。</summary>
/// <param name="Held">差し替え放送の疑いで保留した回(ADR-0021)。失敗ではない(終了コードに影響しない)。</param>
/// <param name="SkippedByDecision">ファイル名の番組名に「取り込まない」と判断されていた回(ADR-0021)。</param>
public sealed record IngestCountsJson(
    int Imported, int Planned, int AlreadyImported, int Unstable, int Junk, int Unsupported, int Failed,
    int Held, int SkippedByDecision);

/// <param name="Outcome">
/// 結果(<c>imported</c>・<c>planned</c>・<c>skippedAlreadyImported</c>・<c>failed</c>・<c>held</c>・<c>skippedByDecision</c> など)。
/// </param>
/// <param name="Source">取込元のファイル。</param>
/// <param name="Output">出力先(取込・予定。出力先に同じ内容が既にあった回はそのファイル)。無ければ null。</param>
/// <param name="Message">失敗の理由、または取込・予定の警告(出力先の同名衝突を別名で避けた等)。無ければ null。</param>
/// <param name="UsedFallback">放送局・番組を解決できず、フォルダ名などから仮に決めたか。</param>
/// <param name="FilenameProgram">
/// 保留(<c>held</c>)・取り込まない判断(<c>skippedByDecision</c>)の回のファイル名の番組名(ADR-0021)。判断は programs.json の
/// <c>FilenamePrograms</c> にこの名前で書く(前方一致・部分一致〈<c>Match.Mode</c>。#240〉なら、この名前の先頭・一部)。ほかは null。
/// </param>
public sealed record IngestItemJson(
    IngestOutcome Outcome, string Source, string? Output, string? Message, bool UsedFallback, string? FilenameProgram);

/// <param name="Status">
/// Jellyfin のライブラリ更新(<c>notNeeded</c>・<c>notConfigured</c>・<c>started</c>・<c>failed</c>・<c>misconfigured</c>)。
/// <c>misconfigured</c> は JellyfinUrl の形式不正(設定を直す。終了コード 1)。
/// </param>
/// <param name="Error">失敗の理由(<c>failed</c>・<c>misconfigured</c> 以外は null)。</param>
public sealed record LibraryRefreshJson(LibraryRefreshStatus Status, string? Error);
