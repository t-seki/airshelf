using Radirec.Core.Ingestion;

namespace Radirec.Cli.Core.Output;

/// <summary><c>ingest --json</c> の <c>result</c>。</summary>
public sealed record IngestJson(
    bool DryRun,
    IngestCountsJson Counts,
    IReadOnlyList<IngestItemJson> Items,
    LibraryRefreshJson LibraryRefresh)
{
    public static IngestJson From(IngestReport report) => new(
        report.DryRun,
        new IngestCountsJson(
            report.Count(IngestOutcome.Imported),
            report.Count(IngestOutcome.Planned),
            report.Count(IngestOutcome.SkippedAlreadyImported),
            report.Count(IngestOutcome.SkippedUnstable),
            report.Count(IngestOutcome.SkippedJunk),
            report.Count(IngestOutcome.SkippedUnsupported),
            report.Count(IngestOutcome.Failed)),
        report.Items.Select(i => new IngestItemJson(i.Outcome, i.SourcePath, i.OutputPath, i.Message, i.UsedFallback)).ToList(),
        new LibraryRefreshJson(report.LibraryRefresh, report.RefreshError));
}

/// <summary>結果ごとの件数(表の下の 1 行と同じ)。</summary>
public sealed record IngestCountsJson(
    int Imported, int Planned, int AlreadyImported, int Unstable, int Junk, int Unsupported, int Failed);

/// <param name="Outcome">結果(<c>imported</c>・<c>planned</c>・<c>skippedAlreadyImported</c>・<c>failed</c> など)。</param>
/// <param name="Source">取込元のファイル。</param>
/// <param name="Output">出力先(取込・予定。出力先に同じ内容が既にあった回はそのファイル)。無ければ null。</param>
/// <param name="Message">失敗の理由、または取込・予定の警告(出力先の同名衝突を別名で避けた等)。無ければ null。</param>
/// <param name="UsedFallback">放送局・番組を解決できず、フォルダ名などから仮に決めたか。</param>
public sealed record IngestItemJson(IngestOutcome Outcome, string Source, string? Output, string? Message, bool UsedFallback);

/// <param name="Status">Jellyfin のライブラリ更新(<c>notNeeded</c>・<c>notConfigured</c>・<c>started</c>・<c>failed</c>)。</param>
/// <param name="Error">失敗の理由(<c>failed</c> 以外は null)。</param>
public sealed record LibraryRefreshJson(LibraryRefreshStatus Status, string? Error);
