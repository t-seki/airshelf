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

    /// <summary>
    /// 差し替え放送の疑いがあり、取り込まずに保留した(ADR-0021)。ファイル名の番組名がフォルダ名も番組のアルバム名(#241)も含まず、判断も無い。
    /// 失敗ではない(終了コードに影響しない)。
    /// </summary>
    Held,

    /// <summary>ファイル名の番組名に「取り込まない」と判断されていたので取り込まない(ADR-0021)。</summary>
    SkippedByDecision,
}

/// <summary>1 ファイルの取り込み結果。</summary>
public sealed record IngestItemResult
{
    public required IngestOutcome Outcome { get; init; }
    public required string SourcePath { get; init; }
    public Episode? Episode { get; init; }
    public string? OutputPath { get; init; }
    public bool UsedFallback { get; init; }

    /// <summary>
    /// 失敗(<see cref="IngestOutcome.Failed"/>)の理由。取込/予定では警告(出力先の同名衝突を別名で避けた等。
    /// ADR-0015)。何も無ければ null。
    /// </summary>
    public string? Message { get; init; }

    /// <summary>処理時に捉えたファイルのフィンガープリント(失敗通知の de-dup 用。ADR-0011)。
    /// <see cref="IngestService"/> の Result() が全 outcome で必ず設定する(欠落しない)。</summary>
    public required FileFingerprint Fingerprint { get; init; }

    /// <summary>
    /// 保留(<see cref="IngestOutcome.Held"/>)・取り込まない判断(<see cref="IngestOutcome.SkippedByDecision"/>)の回の
    /// ファイル名の番組名(CONTEXT.md)。ほかは null。
    /// </summary>
    public string? FilenameProgram { get; init; }
}

/// <summary>取込後の Jellyfin ライブラリ更新の状態。</summary>
public enum LibraryRefreshStatus
{
    /// <summary>更新の必要がない(取込0件・dry-run)。</summary>
    NotNeeded,

    /// <summary>Jellyfin が未設定(JellyfinUrl か JellyfinApiKey が空)のため行わない。</summary>
    NotConfigured,

    /// <summary>更新を起動した(サーバ側で非同期実行。完了は待たない)。</summary>
    Started,

    /// <summary>
    /// 更新の起動に失敗した(繋がらない・エラー・時間切れ。理由は <see cref="IngestReport.RefreshError"/>)。
    /// 時間をおけば直りうる。設定の誤りは <see cref="Misconfigured"/>。
    /// </summary>
    Failed,

    /// <summary>
    /// 設定が不正で更新できない(JellyfinUrl の形式不正。理由は <see cref="IngestReport.RefreshError"/>)。
    /// 設定を直さない限り直らないので <see cref="Failed"/> と分ける(#191)。
    /// </summary>
    Misconfigured,
}

/// <summary>取り込み実行全体の結果。</summary>
public sealed record IngestReport
{
    public required IReadOnlyList<IngestItemResult> Items { get; init; }
    public required bool DryRun { get; init; }

    /// <summary>取込後の Jellyfin ライブラリ更新の状態(取込0件・dry-run 時は <see cref="LibraryRefreshStatus.NotNeeded"/>)。</summary>
    public LibraryRefreshStatus LibraryRefresh { get; init; }

    /// <summary>
    /// ライブラリ更新に失敗したときのメッセージ(<see cref="LibraryRefreshStatus.Failed"/>・
    /// <see cref="LibraryRefreshStatus.Misconfigured"/> 以外は null)。
    /// </summary>
    public string? RefreshError { get; init; }

    /// <summary>
    /// 取込要約シグナルを publisher に渡し、提示(新規取込のトースト)まで届いたか。取込0件・dry-run では要約を渡さない
    /// ので false。要約は取込があれば必ず作られる(提示とは独立した事実)が、publisher が提示への引き渡しを止めたとき
    /// (走査の実行・設定でオフ。<see cref="NotificationSuppressedReason"/>)も false になる(#215)。
    /// 何もしない publisher は理由を返さない。
    /// </summary>
    public bool NotificationPublished { get; init; }

    /// <summary>シグナル発行に失敗したときのメッセージ(成功・未発行時は null)。</summary>
    public string? NotificationError { get; init; }

    /// <summary>
    /// publisher が取込要約を提示に渡さなかった理由(例:<c>CLI の走査</c>・<c>設定でオフ</c>)。提示に渡したとき・
    /// 要約を渡さなかったとき(取込0件・dry-run)・発行に失敗したときは null(#215)。
    /// </summary>
    public string? NotificationSuppressedReason { get; init; }

    /// <summary>
    /// 取込失敗シグナルを発行できたか(新規失敗0件・dry-run では false。ADR-0011)。新規取込のトーストの設定にも、
    /// 走査の実行(#180)にも左右されない(#152)。
    /// </summary>
    public bool FailureNotificationPublished { get; init; }

    /// <summary>失敗シグナル発行に失敗したときのメッセージ(成功・未発行時は null)。</summary>
    public string? FailureNotificationError { get; init; }

    /// <summary>
    /// 保留シグナルを発行できたか(初めて保留した録音ファイルが 0 件・dry-run では false。ADR-0021)。失敗シグナルと同じく、
    /// 新規取込のトーストの設定にも走査の実行にも左右されない。
    /// </summary>
    public bool HeldNotificationPublished { get; init; }

    /// <summary>保留シグナルの発行(または保留のストアの読み書き)に失敗したときのメッセージ(成功・未発行時は null)。</summary>
    public string? HeldNotificationError { get; init; }

    public int Count(IngestOutcome outcome) => Items.Count(i => i.Outcome == outcome);
}
