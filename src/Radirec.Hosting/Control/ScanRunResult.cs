using Radirec.Core.Ingestion;

namespace Radirec.Hosting.Control;

/// <summary>走査の実行が結果を返せなかった理由の種類(CLI の終了コードを決めるのに使う)。</summary>
public enum ScanRunFailure
{
    /// <summary>取り込みが止まっている・止まったので、走査しなかった(または途中で中止した)。</summary>
    Stopped,

    /// <summary>取込元・出力先・Jellyfin などに届かず、走査が途中で失敗した。</summary>
    External,

    /// <summary>それ以外の想定外の失敗。</summary>
    Internal,
}

/// <summary>
/// 走査の実行(CONTEXT.md。ADR-0020 / #180)の結果。常駐が走査を終えたら <see cref="Report"/> が入り、走査できなかった・
/// 途中で失敗したときは <see cref="Error"/> と <see cref="Failure"/> が入る。制御チャネルでそのまま CLI へ返す。
/// </summary>
public sealed record ScanRunResult
{
    /// <summary>走査の結果。失敗・中止したときは null。</summary>
    public ScanRunReport? Report { get; init; }

    /// <summary>走査できなかった・途中で失敗した理由(利用者に見せる文言)。成功したときは null。</summary>
    public string? Error { get; init; }

    /// <summary>失敗の種類。成功したときは null。</summary>
    public ScanRunFailure? Failure { get; init; }

    public static ScanRunResult Ok(ScanRunReport report) => new() { Report = report };

    public static ScanRunResult Failed(ScanRunFailure failure, string error) => new() { Failure = failure, Error = error };
}

/// <summary>
/// 走査 1 回の結果(<see cref="IngestReport"/> から、CLI に見せるものだけを取り出した形)。制御チャネルで JSON にして運ぶので、
/// 台帳の回(<see cref="IngestItemResult.Episode"/>)やフィンガープリントは持たない。<c>ingest --dry-run</c> の結果も同じ形にして
/// 出力する(CLI の表と JSON を 1 つにする)。
/// </summary>
public sealed record ScanRunReport
{
    public required bool DryRun { get; init; }

    public required IReadOnlyList<ScanRunItem> Items { get; init; }

    /// <summary>取込後の Jellyfin ライブラリ更新の状態。</summary>
    public LibraryRefreshStatus LibraryRefresh { get; init; }

    /// <summary>ライブラリ更新に失敗したときのメッセージ(<see cref="LibraryRefreshStatus.Failed"/> 以外は null)。</summary>
    public string? RefreshError { get; init; }

    public int Count(IngestOutcome outcome) => Items.Count(i => i.Outcome == outcome);

    public static ScanRunReport From(IngestReport report) => new()
    {
        DryRun = report.DryRun,
        Items = report.Items
            .Select(i => new ScanRunItem(i.Outcome, i.SourcePath, i.OutputPath, i.Message, i.UsedFallback))
            .ToList(),
        LibraryRefresh = report.LibraryRefresh,
        RefreshError = report.RefreshError,
    };
}

/// <summary>1 ファイルの結果。</summary>
/// <param name="Outcome">結果(取込・予定・済・失敗など)。</param>
/// <param name="SourcePath">取込元のファイル。</param>
/// <param name="OutputPath">出力先(取込・予定。出力先に同じ内容が既にあった回はそのファイル)。無ければ null。</param>
/// <param name="Message">失敗の理由、または取込・予定の警告(出力先の同名衝突を別名で避けた等)。無ければ null。</param>
/// <param name="UsedFallback">放送局・番組を解決できず、フォルダ名などから仮に決めたか。</param>
public sealed record ScanRunItem(
    IngestOutcome Outcome, string SourcePath, string? OutputPath, string? Message, bool UsedFallback);
