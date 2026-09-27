using Radirec.Core.Ingestion;
using Radirec.Core.Normalization;

namespace Radirec.Core.Reapply;

/// <summary>再適用で 1 回分をどう扱うか(dry-run では予定、実行では結果)。ADR-0016。</summary>
public enum ReapplyAction
{
    /// <summary>ファイルを今の設定の場所へ移動・リネームし、タグと台帳を書き換える。</summary>
    Move,

    /// <summary>
    /// ファイルは既に今の設定の場所にあるが、<c>album</c> / <c>title</c> / <c>track</c> が今の設定と違う
    /// (ファイル名だけ手で直した回。#141)。置き場所を変えずにタグを書き換え、台帳を直す。
    /// </summary>
    LedgerAndTags,

    /// <summary>ファイルは既に今の設定の場所にあり、タグも揃っている(手作業で統合済み)。台帳だけを直す。</summary>
    LedgerOnly,

    /// <summary>
    /// 移動先に音声部分が同じファイルが既にある。台帳だけをそちらへ向け直し、元の場所のファイルは消さずに残す
    /// (利用者が確かめて消す)。移動先のファイルの <c>album</c> / <c>title</c> / <c>track</c> が今の設定と違えば
    /// (手作業で統合先へコピーした回)、置き場所を変えずにタグも書き換える(<see cref="ReapplyItem.RewritesTags"/>)。
    /// </summary>
    DuplicateLeft,

    /// <summary>動かさない(移動先に違う内容のファイルがある・放送局が変わった・出力ファイルが無い等)。</summary>
    Excluded,

    /// <summary>
    /// 実行中に失敗した。どこで止まったかにより、元のファイルか移動先のファイルのどちらかが残っている
    /// (確定・元の削除の後に台帳の書き換えで失敗すると、元は消えて移動先にある)。台帳とタグを直す回(タグも直す重複の
    /// 回の移動先も)は、ファイルは同じ場所にあり、確定の前なら元のタグのまま、確定の後ならタグだけ揃っている。
    /// 出力先から消す回(<see cref="Remove"/>)は、ファイルを消せなければファイルも台帳の行も残り、ファイルを消した後に
    /// 台帳の削除で失敗すれば台帳の行だけが残る。次の再適用で揃う。
    /// </summary>
    Failed,

    /// <summary>
    /// ファイル名の番組名に「取り込まない」と判断した回(ADR-0021)。出力先のファイルを消し、台帳の行を消し、空になったフォルダを消す。
    /// 出力ファイルが既に無ければ台帳の行だけを消す(<see cref="ReapplyItem.Reason"/> に添える)。取込元は消さないので、
    /// 判断を戻せば次の走査で取り込み直される。値の並びを変えないよう最後に置く(表示の順は各表示側で決める)。
    /// </summary>
    Remove,
}

/// <summary>再適用の 1 回分。</summary>
public sealed record ReapplyItem
{
    public required ReapplyAction Action { get; init; }

    /// <summary>台帳に記録されている放送局。</summary>
    public required string Station { get; init; }

    /// <summary>台帳に記録されているアルバム名(変更前)。</summary>
    public required string FromAlbum { get; init; }

    /// <summary>
    /// 今の設定で解決したアルバム名(変更後)。出力先から消す回(<see cref="ReapplyAction.Remove"/>)は移動先が無いので、
    /// 今のアルバム(<see cref="FromAlbum"/> と同じ)。
    /// </summary>
    public required string ToAlbum { get; init; }

    public required DateOnly BroadcastDate { get; init; }
    public TimeOnly? BroadcastTime { get; init; }

    /// <summary>台帳に記録されている出力ファイル。</summary>
    public required string FromPath { get; init; }

    /// <summary>
    /// 今の設定での出力ファイル(新しく取り込んだ場合と同じ場所)。出力先から消す回は、消す今の出力ファイル
    /// (<see cref="FromPath"/> と同じ)。
    /// </summary>
    public required string ToPath { get; init; }

    public required string SourcePath { get; init; }

    /// <summary>
    /// 対象外・失敗の理由。重複の注記。出力先から消す回で出力ファイルが既に無いときは、台帳の記録だけを消す旨
    /// (「出力ファイルが見つかりません。台帳の記録だけを消します」)。それ以外は null。
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// 台帳が向く先のファイル(<see cref="ToPath"/>)の <c>album</c> / <c>title</c> / <c>track</c> を、置き場所を変えずに
    /// 書き換えるか。台帳とタグを直す回と、移動先のファイルのタグが古い重複の回(手作業で統合先へコピーした回)で true。
    /// </summary>
    public bool RewritesTags { get; init; }
}

/// <summary>アルバム単位のまとめ(変更前 → 変更後の組ごとの件数)。</summary>
/// <param name="Removals">出力先から消す回(<see cref="ReapplyAction.Remove"/>)。</param>
public sealed record ReapplyAlbumSummary(
    string Station, string FromAlbum, string ToAlbum,
    int Moves, int LedgerAndTags, int LedgerOnly, int DuplicatesLeft, int Excluded, int Failed, int Removals = 0);

/// <summary>
/// 統合設定の無いフォルダを含む兄弟フォルダの組(再適用の確認と、管理画面の要対応で、分割されたままの番組に気づけるようにする。
/// 計算は <see cref="UnintegratedSiblings"/>)。
/// </summary>
/// <param name="FoldersWithoutSetting">組のうち、<c>programs.json</c> の <c>Match.Folders</c> に無いフォルダ。</param>
public sealed record UnintegratedSiblingGroup(SiblingFolderGroup Group, IReadOnlyList<string> FoldersWithoutSetting)
{
    /// <summary>
    /// 組の一部がすでに入っている統合番組のアルバム名(パートの順で最初のフォルダが入っているもの)。どれも統合されていなければ null。
    /// </summary>
    public string? MergedAlbum { get; init; }

    /// <summary>
    /// 分けておく組(<c>KeepSeparate</c>)に記録した組に、あとから加わったフォルダ(W-125。組が変わったので判断し直す)。
    /// 分けておく組に記録していなければ空。
    /// </summary>
    public IReadOnlyList<string> FoldersNewToKeptApart { get; init; } = [];

    /// <summary>分けておく組に記録した組に、新しいフォルダが加わったために出ているか。</summary>
    public bool IsKeptApartGrown => FoldersNewToKeptApart.Count > 0;
}

/// <summary>再適用 1 回の結果。</summary>
public sealed record ReapplyReport
{
    /// <summary>今の設定とずれていた回(揃っている回・radikool の回は含まない)。</summary>
    public required IReadOnlyList<ReapplyItem> Items { get; init; }

    public required bool DryRun { get; init; }

    public required IReadOnlyList<UnintegratedSiblingGroup> UnintegratedSiblings { get; init; }

    /// <summary>
    /// ファイルを書き換えた・消した後の Jellyfin ライブラリ更新(<see cref="ChangesLibrary"/> が偽・dry-run は
    /// <see cref="LibraryRefreshStatus.NotNeeded"/>)。
    /// </summary>
    public LibraryRefreshStatus LibraryRefresh { get; init; }

    public string? RefreshError { get; init; }

    public int Count(ReapplyAction action) => Items.Count(i => i.Action == action);

    /// <summary>ファイルか台帳を変える回(移動・台帳とタグ・台帳のみ・重複・出力先から消す)があるか。</summary>
    public bool HasChanges => Items.Any(i => i.Action is ReapplyAction.Move or ReapplyAction.LedgerAndTags
        or ReapplyAction.LedgerOnly or ReapplyAction.DuplicateLeft or ReapplyAction.Remove);

    /// <summary>
    /// 出力ファイル(置き場所かタグ)を書き換える回の数(移動・台帳とタグ・移動先のタグも直す重複)。メタデータ更新の案内は
    /// これが 1 以上のときに出す。出力先から消す回はタグを書き換えないので含めない(ライブラリ更新は <see cref="ChangesLibrary"/>)。
    /// </summary>
    public int RewritesFiles => Count(ReapplyAction.Move) + DuplicatesRewritingTags + Count(ReapplyAction.LedgerAndTags);

    /// <summary>
    /// Jellyfin のライブラリの中身を変える回があるか(ファイルを書き換えた回か、出力先から消した回)。ライブラリ更新(通常スキャン)は
    /// これが真のときに行う。
    /// </summary>
    public bool ChangesLibrary => RewritesFiles > 0 || Count(ReapplyAction.Remove) > 0;

    /// <summary>重複の回のうち、移動先のファイルのタグも直す回の数。</summary>
    public int DuplicatesRewritingTags => Items.Count(i => i.Action == ReapplyAction.DuplicateLeft && i.RewritesTags);

    /// <summary>
    /// 「揃っています」と示してよいか(CLI とトレイ #126 で同じ判定を使う)。変える回も失敗も無いとき true。
    /// 対象外の回だけが残っていても true で、その一覧は <see cref="Items"/> で添える。
    /// </summary>
    public bool IsAligned => !HasChanges && Count(ReapplyAction.Failed) == 0;

    /// <summary>
    /// 2 つの dry-run の予定が同じか(各回の扱い・放送局・変更前後のアルバムと場所・理由がすべて同じ順で一致)。
    /// トレイ(#126)は実行の直前に dry-run を計算し直し、利用者が確認した予定と違えば実行しない
    /// (確認ダイアログを開いている間にファイルや台帳が変わった場合に、見せていない変更をしないため)。
    /// 兄弟フォルダの一覧は提示だけなので比べない。
    /// </summary>
    public bool HasSamePlanAs(ReapplyReport other) => Items.SequenceEqual(other.Items);

    /// <summary>アルバム単位のまとめ(放送局・変更前・変更後の序数順)。</summary>
    public IReadOnlyList<ReapplyAlbumSummary> Albums => Items
        .GroupBy(i => (i.Station, i.FromAlbum, i.ToAlbum))
        .OrderBy(g => g.Key.Station, StringComparer.Ordinal)
        .ThenBy(g => g.Key.FromAlbum, StringComparer.Ordinal)
        .ThenBy(g => g.Key.ToAlbum, StringComparer.Ordinal)
        .Select(g => new ReapplyAlbumSummary(
            g.Key.Station, g.Key.FromAlbum, g.Key.ToAlbum,
            g.Count(i => i.Action == ReapplyAction.Move),
            g.Count(i => i.Action == ReapplyAction.LedgerAndTags),
            g.Count(i => i.Action == ReapplyAction.LedgerOnly),
            g.Count(i => i.Action == ReapplyAction.DuplicateLeft),
            g.Count(i => i.Action == ReapplyAction.Excluded),
            g.Count(i => i.Action == ReapplyAction.Failed),
            g.Count(i => i.Action == ReapplyAction.Remove)))
        .ToList();
}
