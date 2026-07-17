using Radirec.Core.Ledger;
using Radirec.Core.Model;

namespace Radirec.Core.Ingestion;

/// <summary>1 ファイルの取り込み結果の種別。</summary>
public enum IngestOutcome
{
    /// <summary>取り込んだ(実行時)。</summary>
    Imported,

    /// <summary>取り込み予定(dry-run)。</summary>
    Planned,

    /// <summary>取り込み済みでスキップ(import-once)。</summary>
    SkippedAlreadyImported,

    /// <summary>書き込み中等で未安定のためスキップ。</summary>
    SkippedUnstable,

    /// <summary>スキップパターン一致(ジャンク)でスキップ。</summary>
    SkippedJunk,

    /// <summary>当該 importer が扱えないファイル。</summary>
    SkippedUnsupported,

    /// <summary>パース等に失敗。</summary>
    Failed,
}

/// <summary>1 ファイルの取り込み結果。</summary>
public sealed record IngestItemResult
{
    public required IngestOutcome Outcome { get; init; }
    public required string SourcePath { get; init; }
    public Episode? Episode { get; init; }
    public string? OutputPath { get; init; }
    public bool UsedFallback { get; init; }
    public string? Message { get; init; }

    /// <summary>処理時に捉えたファイルのフィンガープリント(失敗通知の de-dup 用。ADR-0011)。
    /// <see cref="IngestService"/> の Result() が全 outcome で必ず設定する(欠落しない)。</summary>
    public required FileFingerprint Fingerprint { get; init; }
}

/// <summary>取り込み実行全体の結果。</summary>
public sealed record IngestReport
{
    public required IReadOnlyList<IngestItemResult> Items { get; init; }
    public required bool DryRun { get; init; }

    /// <summary>取込後に Jellyfin ライブラリ更新を起動できたか(取込0件・dry-run 時は false)。</summary>
    public bool LibraryRefreshed { get; init; }

    /// <summary>ライブラリ更新に失敗したときのメッセージ(成功・未起動時は null)。</summary>
    public string? RefreshError { get; init; }

    /// <summary>取込要約シグナルを発行できたか(取込0件・dry-run・通知無効時は false)。</summary>
    public bool NotificationPublished { get; init; }

    /// <summary>シグナル発行に失敗したときのメッセージ(成功・未発行時は null)。</summary>
    public string? NotificationError { get; init; }

    /// <summary>取込失敗シグナル(1002)を発行できたか(新規失敗0件・dry-run・通知無効時は false。ADR-0011)。</summary>
    public bool FailureNotificationPublished { get; init; }

    /// <summary>失敗シグナル発行に失敗したときのメッセージ(成功・未発行時は null)。</summary>
    public string? FailureNotificationError { get; init; }

    public int Count(IngestOutcome outcome) => Items.Count(i => i.Outcome == outcome);
}
