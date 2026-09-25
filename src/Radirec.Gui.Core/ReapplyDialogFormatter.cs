using Radirec.Core.Ingestion;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;

namespace Radirec.Gui.Core;

/// <summary>確認・完了の表題(太字の 1 行)と本文。</summary>
public sealed record ReapplyDialogText(string Heading, string Body);

/// <summary>どの設定で再適用を確かめたか。確認の表題と 1 行目の言い方が変わる。</summary>
public enum ReapplyBasis
{
    /// <summary>保存済みの設定(W-252 / W-253)。</summary>
    Saved,

    /// <summary>管理画面の番組ページで編集した、まだ保存していない設定(W-242 / W-243 / W-248。#154)。</summary>
    Draft,
}

/// <summary>
/// 統合設定の再適用(ADR-0016 / #126)の確認・完了の文言と、<c>gui.log</c> に書く明細行を <see cref="ReapplyReport"/> から
/// 組み立てる(表示専用・純粋)。管理画面の番組ページ(#154)が画面の中に出す。語は CLI(<c>radirec reapply</c>)と
/// <c>docs/usage.md</c> の表に揃える。アルバムの行数に上限を持ち、超えた分は「…ほか」に畳んで 1 回ずつの明細は
/// <c>gui.log</c> へ回す。統合設定の無い兄弟フォルダは確認に出さない(管理画面の要対応へ移る。#155)。明細には残す。
/// </summary>
public static class ReapplyDialogFormatter
{
    /// <summary>確認に並べるアルバム(変更前 → 変更後の組)の上限。超えた分は「…ほか N アルバム」(W-245)。</summary>
    public const int MaxAlbums = 8;

    /// <summary>明細の置き場所(利用者が辿れるように、そのまま書く)。</summary>
    public const string LogLocation = @"%LOCALAPPDATA%\RadirecData\gui.log";

    // W-247
    private static readonly string DetailsInLog = $"1 回ずつの明細は gui.log({LogLocation})に書きました。";

    // ---- 確認(dry-run の結果)----

    /// <summary>確認で変える回の数(移動・台帳とタグ・台帳だけ・重複。対象外は含めない)。</summary>
    public static int ChangeCount(ReapplyReport plan)
        => plan.Count(ReapplyAction.Move) + plan.Count(ReapplyAction.LedgerAndTags)
           + plan.Count(ReapplyAction.LedgerOnly) + plan.Count(ReapplyAction.DuplicateLeft);

    /// <summary>
    /// dry-run の結果が揃っている(<see cref="ReapplyReport.IsAligned"/>)ときの表示(W-248)。対象外の回が残っていれば
    /// 件数を添える。保存済みの設定で確かめたとき(<see cref="ReapplyBasis.Saved"/>)の見出しは、来たページに出す 1 行 W-254 の
    /// 1 つ目に使う(#164)。
    /// </summary>
    public static ReapplyDialogText BuildAligned(ReapplyReport plan, ReapplyBasis basis = ReapplyBasis.Saved)
    {
        var excluded = plan.Count(ReapplyAction.Excluded);
        var body = excluded > 0
            ? $"ただし、対象外の回が {excluded} 回あります(衝突・放送局の変更など。動かしません)。\n{DetailsInLog}"
            : "変更するものはありません。";

        var heading = basis == ReapplyBasis.Draft
            ? "取り込み済みの回は編集した設定と揃っています"
            : "取り込み済みの回は今の設定と揃っています";
        return new ReapplyDialogText(heading, body);
    }

    /// <summary>dry-run の結果に変える回があるときの確認の文言(W-242〜W-247 / W-252〜W-253)。</summary>
    public static ReapplyDialogText BuildConfirmation(ReapplyReport plan, ReapplyBasis basis = ReapplyBasis.Saved)
    {
        var changes = ChangeCount(plan);

        var sections = new List<string>
        {
            basis == ReapplyBasis.Draft
                ? "まだ保存していない設定で、取り込み済みの回を次のように揃えます。"
                // W-253。stations.json の「再読み込み」と要対応の「再適用の確認へ」のどちらから来ても同じ言い方にする
                // (再適用は programs.json と stations.json の両方を読み直すので、どちらの入口でも事実として正しい。#164)。
                : "今の programs.json と stations.json の設定で、取り込み済みの回を次のように揃えます。",
            "合計: " + Totals(plan),
            Albums(plan),
        };

        var notes = new List<string>();
        if (plan.Count(ReapplyAction.LedgerAndTags) > 0)
            notes.Add("台帳とタグを直す回は、ファイル名はそのままで、ファイルのタグ(album / title / track)を書き換えます。");
        if (plan.Count(ReapplyAction.DuplicateLeft) > 0)
            notes.Add("重複が残っている回は、移動先に同じ内容のファイルがあるため、元の場所のファイルを消さずに残します(確かめて手で消してください)。");
        if (plan.DuplicatesRewritingTags > 0)
            notes.Add($"重複が残っている回のうち {plan.DuplicatesRewritingTags} 回は、移動先のファイルのタグ(album / title / track)も書き換えます。");
        if (plan.Count(ReapplyAction.Excluded) > 0)
            notes.Add("対象外の回(衝突・放送局の変更など)は動かしません。");
        if (notes.Count > 0)
            sections.Add(string.Join("\n", notes));

        sections.Add(DetailsInLog);

        var heading = basis == ReapplyBasis.Draft
            ? $"{changes} 回を編集した設定に揃えます"
            : $"{changes} 回を今の設定に揃えます";
        return new ReapplyDialogText(heading, Join(sections));
    }

    // ---- 完了(実行の結果)----

    /// <summary>
    /// 実行後の完了表示(W-272〜W-274 / W-247)。件数は確認時の予定ではなく実行の結果から出す(確認を見せている間に
    /// 取り込みが挟まりうるため)。ファイルを書き換えた回(移動・台帳とタグ・タグも直した重複)があれば Jellyfin でのメタデータ更新を案内する
    /// (CLI と同じ条件。ADR-0016)。
    /// </summary>
    public static ReapplyDialogText BuildCompletion(ReapplyReport result)
    {
        var failed = result.Count(ReapplyAction.Failed);
        var heading = failed > 0
            ? $"統合設定の再適用が完了しました(失敗 {failed} 回)"
            : "統合設定の再適用が完了しました";

        var sections = new List<string> { Totals(result) };

        var lines = new List<string>();
        if (Refresh(result) is { } refresh)
            lines.Add(refresh);
        if (result.RewritesFiles > 0)
            lines.Add("タグ(album / title / track)が反映されないアルバムがあれば、Jellyfin でメタデータを更新してください。");
        if (failed > 0)
            lines.Add("失敗した回は、もう一度再適用すると揃います。");
        if (result.Count(ReapplyAction.DuplicateLeft) > 0)
            lines.Add("重複が残っている回は、元の場所のファイルを残しています(確かめて手で消してください)。");
        if (lines.Count > 0)
            sections.Add(string.Join("\n", lines));

        sections.Add(DetailsInLog);

        return new ReapplyDialogText(heading, Join(sections));
    }

    // ---- gui.log の明細 ----

    /// <summary>
    /// <c>gui.log</c> に書く明細行(1 行目がまとめ、以降は 1 回ずつ、最後に兄弟フォルダ)。dry-run と実行の両方で書く。
    /// </summary>
    public static IReadOnlyList<string> BuildLogLines(ReapplyReport report)
    {
        var mode = report.DryRun ? "確認(dry-run)" : "実行";
        var lines = new List<string> { $"統合設定の再適用 {mode}: {Totals(report)}" };

        foreach (var item in report.Items)
        {
            var when = item.BroadcastTime is { } t
                ? $"{item.BroadcastDate:yyyy-MM-dd} {t:HH\\:mm}"
                : item.BroadcastDate.ToString("yyyy-MM-dd");
            var reason = item.Reason is { } r ? $"({r})" : string.Empty;
            lines.Add(
                $"  {Label(item.Action, report.DryRun)} {item.Station} {when} 「{item.FromAlbum}」→「{item.ToAlbum}」 " +
                $"{item.FromPath} → {item.ToPath}{reason}");
        }

        foreach (var sibling in report.UnintegratedSiblings)
            lines.Add($"  統合設定の無い兄弟フォルダ {SiblingLine(sibling)}");

        return lines;
    }

    // ---- 部品 ----

    // 全体の件数。失敗は dry-run では起きないので、あるときだけ出す。
    private static string Totals(ReapplyReport report)
    {
        var parts = new List<string>
        {
            $"移動・リネーム {report.Count(ReapplyAction.Move)} 回",
            $"台帳とタグを直す {report.Count(ReapplyAction.LedgerAndTags)} 回",
            $"台帳だけ直す {report.Count(ReapplyAction.LedgerOnly)} 回",
            $"重複が残っている {report.Count(ReapplyAction.DuplicateLeft)} 回",
            $"対象外 {report.Count(ReapplyAction.Excluded)} 回",
        };
        if (report.Count(ReapplyAction.Failed) > 0)
            parts.Add($"失敗 {report.Count(ReapplyAction.Failed)} 回");
        return string.Join(" / ", parts);
    }

    // アルバム単位のまとめ。長い番組名でも崩れないよう、変更前 → 変更後と件数を別の行にする。
    // 件数は 0 でない種類だけ(アルバムごとに 6 種類並べると読めないため)。
    private static string Albums(ReapplyReport report)
    {
        var albums = report.Albums;
        var lines = new List<string>();
        foreach (var album in albums.Take(MaxAlbums))
        {
            lines.Add($"・{album.Station}「{album.FromAlbum}」→「{album.ToAlbum}」");
            lines.Add("　　" + AlbumCounts(album));
        }

        if (albums.Count > MaxAlbums)
            lines.Add($"…ほか {albums.Count - MaxAlbums} アルバム(gui.log 参照)");

        return string.Join("\n", lines);
    }

    private static string AlbumCounts(ReapplyAlbumSummary album)
    {
        var parts = new List<string>();
        if (album.Moves > 0) parts.Add($"移動・リネーム {album.Moves}");
        if (album.LedgerAndTags > 0) parts.Add($"台帳とタグを直す {album.LedgerAndTags}");
        if (album.LedgerOnly > 0) parts.Add($"台帳だけ直す {album.LedgerOnly}");
        if (album.DuplicatesLeft > 0) parts.Add($"重複が残っている {album.DuplicatesLeft}");
        if (album.Excluded > 0) parts.Add($"対象外 {album.Excluded}");
        if (album.Failed > 0) parts.Add($"失敗 {album.Failed}");
        return string.Join(" / ", parts);
    }

    /// <summary>
    /// 統合設定の無い兄弟フォルダの 1 組(W-123:<c>{局}: {フォルダ} / {フォルダ}(設定なし)</c>)。再適用の確認と、
    /// 管理画面の要対応で同じものを使う。
    /// </summary>
    public static string SiblingLine(UnintegratedSiblingGroup sibling)
    {
        var folders = string.Join(" / ", sibling.Group.Folders.Select(f =>
            sibling.FoldersWithoutSetting.Contains(f) ? $"{f}(設定なし)" : f));
        return $"{sibling.Group.Station}: {folders}";
    }

    // 移動した後の Jellyfin ライブラリ更新。移動が無ければ(NotNeeded)出さない。
    private static string? Refresh(ReapplyReport report) => report.LibraryRefresh switch
    {
        LibraryRefreshStatus.Started => "Jellyfin ライブラリ更新: 起動しました",
        LibraryRefreshStatus.NotConfigured => "Jellyfin ライブラリ更新: 未設定のため行いません",
        LibraryRefreshStatus.Failed => $"Jellyfin ライブラリ更新に失敗しました: {report.RefreshError ?? "理由不明"}",
        _ => null,
    };

    private static string Label(ReapplyAction action, bool dryRun) => action switch
    {
        ReapplyAction.Move => dryRun ? "移動予定" : "移動",
        ReapplyAction.LedgerAndTags => "台帳とタグ",
        ReapplyAction.LedgerOnly => "台帳のみ",
        ReapplyAction.DuplicateLeft => "重複",
        ReapplyAction.Excluded => "対象外",
        ReapplyAction.Failed => "失敗",
        _ => action.ToString(),
    };

    private static string Join(IEnumerable<string> sections) => string.Join("\n\n", sections);
}
