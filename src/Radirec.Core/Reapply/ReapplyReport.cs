using Radirec.Core.Ingestion;
using Radirec.Core.Normalization;

namespace Radirec.Core.Reapply;

/// <summary>再適用で 1 回分をどう扱うか(dry-run では予定、実行では結果)。ADR-0016。</summary>
public enum ReapplyAction
{
    /// <summary>ファイルを今の設定の場所へ移動・リネームし、タグと台帳を書き換える。</summary>
    Move,

    /// <summary>ファイルは既に今の設定の場所にある(手作業で統合済み)。台帳だけを直す。</summary>
    LedgerOnly,

    /// <summary>
    /// 移動先に音声部分が同じファイルが既にある。台帳だけをそちらへ向け直し、元の場所のファイルは消さずに残す
    /// (利用者が確かめて消す)。
    /// </summary>
    DuplicateLeft,

    /// <summary>動かさない(移動先に違う内容のファイルがある・放送局が変わった・出力ファイルが無い等)。</summary>
    Excluded,

    /// <summary>
    /// 実行中に失敗した。どこで止まったかにより、元のファイルか移動先のファイルのどちらかが残っている
    /// (確定・元の削除の後に台帳の書き換えで失敗すると、元は消えて移動先にある)。次の再適用で揃う。
    /// </summary>
    Failed,
}

/// <summary>再適用の 1 回分。</summary>
public sealed record ReapplyItem
{
    public required ReapplyAction Action { get; init; }

    /// <summary>台帳に記録されている放送局。</summary>
    public required string Station { get; init; }

    /// <summary>台帳に記録されているアルバム名(変更前)。</summary>
    public required string FromAlbum { get; init; }

    /// <summary>今の設定で解決したアルバム名(変更後)。</summary>
    public required string ToAlbum { get; init; }

    public required DateOnly BroadcastDate { get; init; }
    public TimeOnly? BroadcastTime { get; init; }

    /// <summary>台帳に記録されている出力ファイル。</summary>
    public required string FromPath { get; init; }

    /// <summary>今の設定での出力ファイル(新しく取り込んだ場合と同じ場所)。</summary>
    public required string ToPath { get; init; }

    public required string SourcePath { get; init; }

    /// <summary>対象外・失敗の理由。重複の注記。それ以外は null。</summary>
    public string? Reason { get; init; }
}

/// <summary>アルバム単位のまとめ(変更前 → 変更後の組ごとの件数)。</summary>
public sealed record ReapplyAlbumSummary(
    string Station, string FromAlbum, string ToAlbum,
    int Moves, int LedgerOnly, int DuplicatesLeft, int Excluded, int Failed);

/// <summary>
/// 統合設定の無いフォルダを含む兄弟フォルダの組(再適用の確認で、分割されたままの番組に気づけるようにする)。
/// </summary>
/// <param name="FoldersWithoutSetting">組のうち、<c>programs.json</c> の <c>Match.Folders</c> に無いフォルダ。</param>
public sealed record UnintegratedSiblingGroup(SiblingFolderGroup Group, IReadOnlyList<string> FoldersWithoutSetting);

/// <summary>再適用 1 回の結果。</summary>
public sealed record ReapplyReport
{
    /// <summary>今の設定とずれていた回(揃っている回・radikool の回は含まない)。</summary>
    public required IReadOnlyList<ReapplyItem> Items { get; init; }

    public required bool DryRun { get; init; }

    public required IReadOnlyList<UnintegratedSiblingGroup> UnintegratedSiblings { get; init; }

    /// <summary>移動した後の Jellyfin ライブラリ更新(移動 0 件・dry-run は <see cref="LibraryRefreshStatus.NotNeeded"/>)。</summary>
    public LibraryRefreshStatus LibraryRefresh { get; init; }

    public string? RefreshError { get; init; }

    public int Count(ReapplyAction action) => Items.Count(i => i.Action == action);

    /// <summary>ファイルか台帳を変える回(移動・台帳のみ・重複)があるか。</summary>
    public bool HasChanges => Items.Any(i => i.Action is ReapplyAction.Move or ReapplyAction.LedgerOnly or ReapplyAction.DuplicateLeft);

    /// <summary>
    /// 「揃っています」と示してよいか(CLI とトレイ #126 で同じ判定を使う)。変える回も失敗も無いとき true。
    /// 対象外の回だけが残っていても true で、その一覧は <see cref="Items"/> で添える。
    /// </summary>
    public bool IsAligned => !HasChanges && Count(ReapplyAction.Failed) == 0;

    /// <summary>アルバム単位のまとめ(放送局・変更前・変更後の序数順)。</summary>
    public IReadOnlyList<ReapplyAlbumSummary> Albums => Items
        .GroupBy(i => (i.Station, i.FromAlbum, i.ToAlbum))
        .OrderBy(g => g.Key.Station, StringComparer.Ordinal)
        .ThenBy(g => g.Key.FromAlbum, StringComparer.Ordinal)
        .ThenBy(g => g.Key.ToAlbum, StringComparer.Ordinal)
        .Select(g => new ReapplyAlbumSummary(
            g.Key.Station, g.Key.FromAlbum, g.Key.ToAlbum,
            g.Count(i => i.Action == ReapplyAction.Move),
            g.Count(i => i.Action == ReapplyAction.LedgerOnly),
            g.Count(i => i.Action == ReapplyAction.DuplicateLeft),
            g.Count(i => i.Action == ReapplyAction.Excluded),
            g.Count(i => i.Action == ReapplyAction.Failed)))
        .ToList();
}
