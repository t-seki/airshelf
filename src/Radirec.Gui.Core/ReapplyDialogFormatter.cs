using Radirec.Core.Ingestion;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;

namespace Radirec.Gui.Core;

/// <summary>ダイアログの表題(太字の 1 行)と本文。</summary>
public sealed record ReapplyDialogText(string Heading, string Body);

/// <summary>
/// トレイからの統合設定の再適用(ADR-0016 / #126)で出すダイアログの文言と、<c>gui.log</c> に書く明細行を
/// <see cref="ReapplyReport"/> から組み立てる(表示専用・純粋)。語は CLI(<c>radirec reapply</c>)と
/// <c>docs/usage.md</c> の表に揃える。ダイアログはスクロールしないので、アルバムと兄弟フォルダの行数に上限を持ち、
/// 超えた分は「…ほか」に畳んで 1 回ずつの明細は <c>gui.log</c> へ回す。
/// </summary>
public static class ReapplyDialogFormatter
{
    /// <summary>確認ダイアログに並べるアルバム(変更前 → 変更後の組)の上限。超えた分は「…ほか N アルバム」。</summary>
    public const int MaxAlbums = 8;

    /// <summary>ダイアログに並べる、統合設定の無い兄弟フォルダの組の上限。</summary>
    public const int MaxSiblingGroups = 5;

    /// <summary>明細の置き場所(利用者が辿れるように、ダイアログにそのまま書く)。</summary>
    public const string LogLocation = @"%LOCALAPPDATA%\RadirecData\gui.log";

    private static readonly string DetailsInLog = $"1 回ずつの明細は gui.log({LogLocation})に書きました。";

    // ---- 確認(dry-run の結果)----

    /// <summary>
    /// dry-run の結果が揃っている(<see cref="ReapplyReport.IsAligned"/>)ときの表示。対象外の回が残っていれば件数を添え、
    /// 統合設定の無い兄弟フォルダも示す(揃っていても、分割されたままの番組に気づけるように)。
    /// </summary>
    /// <param name="canAppend">[統合設定を追記して開く] を出すか(兄弟フォルダの段に案内を 1 行添える。#144)。</param>
    public static ReapplyDialogText BuildAligned(ReapplyReport plan, bool canAppend = false)
    {
        var sections = new List<string>();

        var excluded = plan.Count(ReapplyAction.Excluded);
        if (excluded > 0)
            sections.Add($"ただし、対象外の回が {excluded} 回あります(衝突・放送局の変更など。動かしません)。\n{DetailsInLog}");
        else
            sections.Add("変更するものはありません。");

        if (Siblings(plan, canAppend) is { } siblings)
            sections.Add(siblings);

        return new ReapplyDialogText("取り込み済みの回は今の設定と揃っています", Join(sections));
    }

    /// <summary>dry-run の結果に変える回があるときの確認ダイアログ([適用] / [キャンセル])の文言。</summary>
    /// <param name="canAppend">[統合設定を追記して開く] を出すか(兄弟フォルダの段に案内を 1 行添える。#144)。</param>
    public static ReapplyDialogText BuildConfirmation(ReapplyReport plan, bool canAppend = false)
    {
        var changes = plan.Count(ReapplyAction.Move) + plan.Count(ReapplyAction.LedgerAndTags)
            + plan.Count(ReapplyAction.LedgerOnly) + plan.Count(ReapplyAction.DuplicateLeft);

        var sections = new List<string>
        {
            "今の programs.json の設定で、取り込み済みの回を次のように揃えます。",
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

        if (Siblings(plan, canAppend) is { } siblings)
            sections.Add(siblings);

        sections.Add(DetailsInLog);

        return new ReapplyDialogText($"{changes} 回を今の設定に揃えます", Join(sections));
    }

    // ---- 完了(実行の結果)----

    /// <summary>
    /// 実行後の完了表示。件数は確認時の予定ではなく実行の結果から出す(確認ダイアログを開いている間に取り込みが
    /// 挟まりうるため)。ファイルを書き換えた回(移動・台帳とタグ・タグも直した重複)があれば Jellyfin でのメタデータ更新を案内する
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

    // ---- 統合設定の追記(#144)----

    /// <summary>確認ダイアログの「統合設定の無い兄弟フォルダ」から、提案を programs.json に追記して開くボタン。</summary>
    public const string AppendButton = "統合設定を追記して開く";

    /// <summary>追記した後に出すバルーン(その回の再適用は行わないので、保存してから選び直してもらう)。</summary>
    public static string AppendedBalloon(string fileName)
        => $"{fileName} に統合設定の提案を追記しました。確かめて保存したら、もう一度「統合設定の再適用…」を選んでください";

    /// <summary>
    /// 追記の確認([追記] / [キャンセル])。何を追記するかを組ごとに 1 行で示す(追記する中身そのものはダイアログの
    /// 「詳細」に出す)。<paramref name="proposals"/> は追記する全部(確認ダイアログの兄弟フォルダの上限では切らない)。
    /// </summary>
    public static ReapplyDialogText BuildAppendConfirmation(IReadOnlyList<SiblingProgramProposal> proposals, string fileName)
    {
        var lines = proposals.Take(MaxAppendLines).Select(p => p.JoinsExisting
            ? $"・{p.Station} 統合番組『{p.Album}』に足すフォルダ {p.Folders.Count} つ(コメントで案内します。既存の設定は変えません)"
            : $"・{p.Station}「{p.Album}」: {p.Folders.Count} フォルダを統合する設定").ToList();
        if (proposals.Count > MaxAppendLines)
            lines.Add($"…ほか {proposals.Count - MaxAppendLines} 組");

        var sections = new List<string>
        {
            string.Join("\n", lines),
            $"追記する中身は「詳細」にあります。追記の前に、同じフォルダへバックアップ({fileName}.bak-日時)を作ります。",
            "追記したらエディタで開きます。確かめて直し、保存してから、もう一度「統合設定の再適用…」を選んでください" +
            "(今回の再適用は行いません)。",
        };
        return new ReapplyDialogText($"次の {proposals.Count} 組を {fileName} に追記して開きます", Join(sections));
    }

    /// <summary>今の programs.json では、どの組にも統合設定の無いフォルダが残っていない(dry-run の後に直した等)。</summary>
    public static ReapplyDialogText BuildNothingToAppend(string fileName) => new(
        "追記するものはありません",
        $"{fileName} には、兄弟フォルダの統合設定が既にあります。{fileName} を開きます。\n\n" +
        "保存済みなら、もう一度「統合設定の再適用…」を選んでください。");

    /// <summary>追記できなかった(壊れている・"Programs" の配列が無い・確認中に変わった等)。何も書かずにファイルだけ開く。</summary>
    public static ReapplyDialogText BuildAppendFailed(string fileName, string reason) => new(
        $"{fileName} に追記できませんでした",
        $"{reason}\n\n{fileName} には何も書き込んでいません。{fileName} を開きます。");

    // 追記の確認に並べる組の上限(中身は「詳細」に全部ある)。
    private const int MaxAppendLines = 8;

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

    // 統合設定の無い兄弟フォルダ(#127 の補い)。無ければ null。
    private static string? Siblings(ReapplyReport report, bool canAppend)
    {
        var groups = report.UnintegratedSiblings;
        if (groups.Count == 0)
            return null;

        var lines = new List<string> { "統合設定の無い兄弟フォルダ(programs.json で統合するか確かめてください):" };
        lines.AddRange(groups.Take(MaxSiblingGroups).Select(g => "・" + SiblingLine(g)));
        if (groups.Count > MaxSiblingGroups)
            lines.Add($"…ほか {groups.Count - MaxSiblingGroups} 組(gui.log 参照)");
        if (canAppend)
            lines.Add($"[{AppendButton}] で、統合設定の提案を programs.json に追記して開けます(今回の再適用は行いません)。");
        return string.Join("\n", lines);
    }

    private static string SiblingLine(UnintegratedSiblingGroup sibling)
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
