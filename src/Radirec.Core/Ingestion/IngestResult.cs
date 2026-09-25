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

    /// <summary>
    /// 失敗(<see cref="IngestOutcome.Failed"/>)の理由。取込/予定では警告(出力先の同名衝突を別名で避けた等。
    /// ADR-0015)。何も無ければ null。
    /// </summary>
    public string? Message { get; init; }

    /// <summary>処理時に捉えたファイルのフィンガープリント(失敗通知の de-dup 用。ADR-0011)。
    /// <see cref="IngestService"/> の Result() が全 outcome で必ず設定する(欠落しない)。</summary>
    public required FileFingerprint Fingerprint { get; init; }
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

    /// <summary>更新の起動に失敗した(理由は <see cref="IngestReport.RefreshError"/>)。JellyfinUrl の形式不正も含む。</summary>
    Failed,
}

/// <summary>取り込み実行全体の結果。</summary>
public sealed record IngestReport
{
    public required IReadOnlyList<IngestItemResult> Items { get; init; }
    public required bool DryRun { get; init; }

    /// <summary>取込後の Jellyfin ライブラリ更新の状態(取込0件・dry-run 時は <see cref="LibraryRefreshStatus.NotNeeded"/>)。</summary>
    public LibraryRefreshStatus LibraryRefresh { get; init; }

    /// <summary>ライブラリ更新に失敗したときのメッセージ(<see cref="LibraryRefreshStatus.Failed"/> 以外は null)。</summary>
    public string? RefreshError { get; init; }

    /// <summary>
    /// 取込要約シグナルを発行できたか(取込0件・dry-run・非発行走査では false)。常駐で新規取込のトーストを止めていても、
    /// シグナルは発行する(トーストを出さないのは提示側。#152)。
    /// </summary>
    public bool NotificationPublished { get; init; }

    /// <summary>シグナル発行に失敗したときのメッセージ(成功・未発行時は null)。</summary>
    public string? NotificationError { get; init; }

    /// <summary>
    /// 取込失敗シグナルを発行できたか(新規失敗0件・dry-run・非発行走査では false。ADR-0011)。新規取込のトーストの設定には
    /// 左右されない(#152)。
    /// </summary>
    public bool FailureNotificationPublished { get; init; }

    /// <summary>失敗シグナル発行に失敗したときのメッセージ(成功・未発行時は null)。</summary>
    public string? FailureNotificationError { get; init; }

    /// <summary>
    /// 初めて取り込んだフォルダのうち、統合設定が無く兄弟フォルダのあるもの(#127)。
    /// 取込0件・dry-run では空。**打ち切らない**(トレイはここから <c>gui.log</c> に全件を書く)。取込要約シグナルに載せる分は
    /// <see cref="IngestSummary.MaxSiblingFolderNotices"/> 件まで。
    /// </summary>
    public IReadOnlyList<SiblingFolderNotice> SiblingFolderNotices { get; init; } = [];

    /// <summary>兄弟フォルダの判定に失敗したときのメッセージ(取り込みは成功している。成功・未判定時は null)。</summary>
    public string? SiblingFolderCheckError { get; init; }

    public int Count(IngestOutcome outcome) => Items.Count(i => i.Outcome == outcome);
}
