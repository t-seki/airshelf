using Radirec.Core.Ingestion;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;
using Radirec.Gui.Core;

namespace Radirec.Tests;

/// <summary>再適用(#126)の確認・完了の文言(管理画面の番組ページ。#154)と gui.log の明細行。</summary>
public sealed class ReapplyDialogFormatterTests
{
    private const string Station = "TBSラジオ";
    private const string Long = "土曜ワイドラジオTOKYOナイツのちゃきちゃき大放送";

    private static ReapplyItem Item(
        ReapplyAction action, string from = Long + "(1)", string to = Long, string? reason = null,
        string station = Station, TimeOnly? time = null) => new()
    {
        Action = action,
        Station = station,
        FromAlbum = from,
        ToAlbum = to,
        BroadcastDate = new DateOnly(2026, 9, 19),
        BroadcastTime = time,
        FromPath = $@"Z:\{from}\{from} 2026-09-19.m4a",
        ToPath = $@"Z:\{to}\{to} 2026-09-19-1.m4a",
        SourcePath = $@"I:\{station}\{from}\x.m4a",
        Reason = reason,
    };

    private static ReapplyReport Report(
        bool dryRun, IEnumerable<ReapplyItem> items, IReadOnlyList<UnintegratedSiblingGroup>? siblings = null,
        LibraryRefreshStatus refresh = LibraryRefreshStatus.NotNeeded, string? refreshError = null) => new()
    {
        Items = items.ToList(),
        DryRun = dryRun,
        UnintegratedSiblings = siblings ?? [],
        LibraryRefresh = refresh,
        RefreshError = refreshError,
    };

    private static UnintegratedSiblingGroup Sibling(string baseName, params string[] withoutSetting) =>
        new(new SiblingFolderGroup(Station, baseName, [baseName + "(1)", baseName + "(2)"]), withoutSetting);

    private static IEnumerable<ReapplyItem> Repeat(int count, ReapplyAction action, string from = Long + "(1)") =>
        Enumerable.Range(0, count).Select(_ => Item(action, from));

    // ---- 揃っている ----

    [Fact]
    public void 何も無ければ揃っていますだけ()
    {
        var text = ReapplyDialogFormatter.BuildAligned(Report(true, []));

        Assert.Equal("取り込み済みの回は今の設定と揃っています", text.Heading);
        Assert.Equal("変更するものはありません。", text.Body);
    }

    [Fact]
    public void 対象外だけが残っていれば件数と明細の場所を添える()
    {
        var plan = Report(true, Repeat(2, ReapplyAction.Excluded));
        Assert.True(plan.IsAligned);

        var text = ReapplyDialogFormatter.BuildAligned(plan);

        Assert.Equal("取り込み済みの回は今の設定と揃っています", text.Heading);
        Assert.Contains("対象外の回が 2 回あります", text.Body);
        Assert.Contains(ReapplyDialogFormatter.LogLocation, text.Body);
    }

    [Fact]
    public void 統合設定の無い兄弟フォルダは確認に出さない()
    {
        // 要対応へ移る(#155)。モックの確認(W-241〜W-250)にも無い。明細(gui.log)には残す(下の明細行のテスト)。
        var siblings = new[] { Sibling("番組X", "番組X(2)") };

        Assert.DoesNotContain("兄弟フォルダ", ReapplyDialogFormatter.BuildAligned(Report(true, [], siblings)).Body);
        Assert.DoesNotContain("兄弟フォルダ",
            ReapplyDialogFormatter.BuildConfirmation(Report(true, [Item(ReapplyAction.Move)], siblings)).Body);
    }

    [Fact]
    public void 編集した設定の確認は表題と1行目で保存前だと分かる()
    {
        var plan = Report(true, [.. Repeat(3, ReapplyAction.Move)]);

        var draft = ReapplyDialogFormatter.BuildConfirmation(plan, ReapplyBasis.Draft);
        var saved = ReapplyDialogFormatter.BuildConfirmation(plan, ReapplyBasis.Saved);

        Assert.Equal("3 回を編集した設定に揃えます", draft.Heading); // W-242
        Assert.StartsWith("まだ保存していない設定で、取り込み済みの回を次のように揃えます。", draft.Body); // W-243
        Assert.Equal("3 回を今の設定に揃えます", saved.Heading); // W-252
        Assert.StartsWith("今の programs.json の設定で、取り込み済みの回を次のように揃えます。", saved.Body); // W-253
        Assert.Equal(3, ReapplyDialogFormatter.ChangeCount(plan));

        var aligned = ReapplyDialogFormatter.BuildAligned(Report(true, []), ReapplyBasis.Draft);
        Assert.Equal("取り込み済みの回は編集した設定と揃っています", aligned.Heading); // W-248
        Assert.Equal("変更するものはありません。", aligned.Body);
    }

    // ---- 確認 ----

    [Fact]
    public void 確認はアルバムごとに変更前後と0でない件数を並べる()
    {
        var plan = Report(true,
        [
            .. Repeat(12, ReapplyAction.Move),
            Item(ReapplyAction.DuplicateLeft, reason: "移動先に同じ内容のファイルがあるため、元の場所のファイルを残しています"),
            .. Repeat(3, ReapplyAction.LedgerOnly, from: Long + "(2)"),
        ]);

        var text = ReapplyDialogFormatter.BuildConfirmation(plan);

        // 変える回 = 移動 + 台帳のみ + 重複(対象外は含めない)
        Assert.Equal("16 回を今の設定に揃えます", text.Heading);
        Assert.Contains("合計: 移動・リネーム 12 回 / 台帳とタグを直す 0 回 / 台帳だけ直す 3 回 / 重複が残っている 1 回 / 対象外 0 回", text.Body);
        Assert.Contains($"・TBSラジオ「{Long}(1)」→「{Long}」\n　　移動・リネーム 12 / 重複が残っている 1", text.Body);
        Assert.Contains($"・TBSラジオ「{Long}(2)」→「{Long}」\n　　台帳だけ直す 3", text.Body);
        Assert.Contains("元の場所のファイルを消さずに残します", text.Body);
        Assert.DoesNotContain("対象外の回", text.Body);
        Assert.DoesNotContain("台帳とタグを直す回は", text.Body);
        Assert.EndsWith($"1 回ずつの明細は gui.log({ReapplyDialogFormatter.LogLocation})に書きました。", text.Body);
    }

    [Fact]
    public void 確認で台帳とタグを直す回を別の区分として移動の次に出す()
    {
        var plan = Report(true,
        [
            Item(ReapplyAction.Move),
            .. Repeat(2, ReapplyAction.LedgerAndTags),
            Item(ReapplyAction.LedgerOnly),
        ]);
        Assert.False(plan.IsAligned);

        var text = ReapplyDialogFormatter.BuildConfirmation(plan);

        // 変える回に台帳とタグを直す回も含む
        Assert.Equal("4 回を今の設定に揃えます", text.Heading);
        Assert.Contains("合計: 移動・リネーム 1 回 / 台帳とタグを直す 2 回 / 台帳だけ直す 1 回 / 重複が残っている 0 回 / 対象外 0 回", text.Body);
        Assert.Contains($"・TBSラジオ「{Long}(1)」→「{Long}」\n　　移動・リネーム 1 / 台帳とタグを直す 2 / 台帳だけ直す 1", text.Body);
        // NAS 上のファイルのタグを書き換えることが確認の段階で分かる
        Assert.Contains("台帳とタグを直す回は、ファイル名はそのままで、ファイルのタグ(album / title / track)を書き換えます。", text.Body);
    }

    [Fact]
    public void 確認で移動先のタグも直す重複の回があれば件数を添える()
    {
        var plan = Report(true,
        [
            Item(ReapplyAction.DuplicateLeft) with { RewritesTags = true },
            Item(ReapplyAction.DuplicateLeft),
        ]);

        var text = ReapplyDialogFormatter.BuildConfirmation(plan);

        Assert.Contains("重複が残っている回のうち 1 回は、移動先のファイルのタグ(album / title / track)も書き換えます。", text.Body);
    }

    [Fact]
    public void 完了で移動先のタグを直した重複の回があればメタデータ更新を案内する()
    {
        var withTags = ReapplyDialogFormatter.BuildCompletion(
            Report(false, [Item(ReapplyAction.DuplicateLeft) with { RewritesTags = true }]));
        var without = ReapplyDialogFormatter.BuildCompletion(Report(false, [Item(ReapplyAction.DuplicateLeft)]));

        Assert.Contains("Jellyfin でメタデータを更新してください。", withTags.Body);
        Assert.DoesNotContain("メタデータ", without.Body);
    }

    [Fact]
    public void 確認で失敗は0なら出さない()
    {
        var text = ReapplyDialogFormatter.BuildConfirmation(Report(true, Repeat(1, ReapplyAction.Move)));

        Assert.DoesNotContain("失敗", text.Body);
    }

    [Fact]
    public void 確認で対象外があれば動かさない旨を添える()
    {
        var plan = Report(true,
        [
            Item(ReapplyAction.Move),
            Item(ReapplyAction.Excluded, reason: "移動先に内容の違うファイルがあります(衝突のため対象外)"),
        ]);

        var text = ReapplyDialogFormatter.BuildConfirmation(plan);

        Assert.Equal("1 回を今の設定に揃えます", text.Heading);
        Assert.Contains("移動・リネーム 1 / 対象外 1", text.Body);
        Assert.Contains("対象外の回(衝突・放送局の変更など)は動かしません。", text.Body);
    }

    [Fact]
    public void アルバムが上限を超えたらほかに畳む()
    {
        var count = ReapplyDialogFormatter.MaxAlbums + 3;
        var plan = Report(true, Enumerable.Range(1, count)
            .Select(i => Item(ReapplyAction.Move, from: $"番組{i:D2}(1)", to: $"番組{i:D2}")));

        var text = ReapplyDialogFormatter.BuildConfirmation(plan);

        Assert.Contains("「番組01(1)」→「番組01」", text.Body);
        Assert.Contains($"「番組{ReapplyDialogFormatter.MaxAlbums:D2}(1)」", text.Body);
        Assert.DoesNotContain($"「番組{ReapplyDialogFormatter.MaxAlbums + 1:D2}(1)」", text.Body);
        Assert.Contains("…ほか 3 アルバム(gui.log 参照)", text.Body);
        // 合計は畳んだ分も含めた実数
        Assert.Contains($"移動・リネーム {count} 回", text.Body);
    }

    // ---- 完了 ----

    [Fact]
    public void 完了は実行結果の件数とJellyfinの案内を出す()
    {
        var result = Report(false, [.. Repeat(5, ReapplyAction.Move), Item(ReapplyAction.LedgerOnly)],
            refresh: LibraryRefreshStatus.Started);

        var text = ReapplyDialogFormatter.BuildCompletion(result);

        Assert.Equal("統合設定の再適用が完了しました", text.Heading);
        Assert.StartsWith("移動・リネーム 5 回 / 台帳とタグを直す 0 回 / 台帳だけ直す 1 回 / 重複が残っている 0 回 / 対象外 0 回", text.Body);
        Assert.Contains("Jellyfin ライブラリ更新: 起動しました", text.Body);
        Assert.Contains("タグ(album / title / track)が反映されないアルバムがあれば、Jellyfin でメタデータを更新してください。", text.Body);
        Assert.DoesNotContain("失敗", text.Body);
    }

    [Fact]
    public void 台帳とタグを直した回だけでもメタデータ更新を案内する()
    {
        var text = ReapplyDialogFormatter.BuildCompletion(
            Report(false, [Item(ReapplyAction.LedgerAndTags)], refresh: LibraryRefreshStatus.Started));

        Assert.StartsWith("移動・リネーム 0 回 / 台帳とタグを直す 1 回 / 台帳だけ直す 0 回", text.Body);
        Assert.Contains("Jellyfin ライブラリ更新: 起動しました", text.Body);
        Assert.Contains("Jellyfin でメタデータを更新してください。", text.Body);
    }

    [Fact]
    public void ファイルを書き換えていなければメタデータ更新は案内しない()
    {
        var text = ReapplyDialogFormatter.BuildCompletion(Report(false, [Item(ReapplyAction.LedgerOnly)]));

        Assert.DoesNotContain("メタデータ", text.Body);
        Assert.DoesNotContain("Jellyfin", text.Body);
    }

    [Fact]
    public void 失敗があれば表題と本文で知らせる()
    {
        var result = Report(false, [Item(ReapplyAction.Move), Item(ReapplyAction.Failed, reason: "アクセスが拒否されました")],
            refresh: LibraryRefreshStatus.Started);

        var text = ReapplyDialogFormatter.BuildCompletion(result);

        Assert.Equal("統合設定の再適用が完了しました(失敗 1 回)", text.Heading);
        Assert.Contains("/ 失敗 1 回", text.Body);
        Assert.Contains("失敗した回は、もう一度再適用すると揃います。", text.Body);
    }

    [Theory]
    [InlineData(LibraryRefreshStatus.NotConfigured, "Jellyfin ライブラリ更新: 未設定のため行いません")]
    [InlineData(LibraryRefreshStatus.Failed, "Jellyfin ライブラリ更新に失敗しました: 接続できません")]
    public void Jellyfinの更新結果を出す(LibraryRefreshStatus status, string expected)
    {
        var text = ReapplyDialogFormatter.BuildCompletion(
            Report(false, [Item(ReapplyAction.Move)], refresh: status, refreshError: "接続できません"));

        Assert.Contains(expected, text.Body);
    }

    [Fact]
    public void 完了で重複が残っていれば手で消す旨を添える()
    {
        var text = ReapplyDialogFormatter.BuildCompletion(Report(false, [Item(ReapplyAction.DuplicateLeft)]));

        Assert.Contains("重複が残っている回は、元の場所のファイルを残しています", text.Body);
    }

    // ---- gui.log ----

    [Fact]
    public void 明細行はまとめと1回ずつと兄弟フォルダ()
    {
        var plan = Report(true,
        [
            Item(ReapplyAction.Move, time: new TimeOnly(13, 0)),
            Item(ReapplyAction.Excluded, reason: "出力ファイルが見つかりません"),
        ],
        [Sibling("番組X", "番組X(2)")]);

        var lines = ReapplyDialogFormatter.BuildLogLines(plan);

        Assert.Equal(4, lines.Count);
        Assert.Equal("統合設定の再適用 確認(dry-run): 移動・リネーム 1 回 / 台帳とタグを直す 0 回 / 台帳だけ直す 0 回 / 重複が残っている 0 回 / 対象外 1 回", lines[0]);
        Assert.Equal(
            $@"  移動予定 TBSラジオ 2026-09-19 13:00 「{Long}(1)」→「{Long}」 Z:\{Long}(1)\{Long}(1) 2026-09-19.m4a → Z:\{Long}\{Long} 2026-09-19-1.m4a",
            lines[1]);
        Assert.StartsWith("  対象外 TBSラジオ 2026-09-19 「", lines[2]);
        Assert.EndsWith("(出力ファイルが見つかりません)", lines[2]);
        Assert.Equal("  統合設定の無い兄弟フォルダ TBSラジオ: 番組X(1) / 番組X(2)(設定なし)", lines[3]);
    }

    [Fact]
    public void 明細行で台帳とタグを直す回は台帳のみと区別する()
    {
        var lines = ReapplyDialogFormatter.BuildLogLines(
            Report(false, [Item(ReapplyAction.LedgerAndTags), Item(ReapplyAction.LedgerOnly)]));

        Assert.StartsWith("  台帳とタグ TBSラジオ 2026-09-19 「", lines[1]);
        Assert.StartsWith("  台帳のみ TBSラジオ 2026-09-19 「", lines[2]);
    }

    [Fact]
    public void 実行の明細は移動と書く()
    {
        var lines = ReapplyDialogFormatter.BuildLogLines(Report(false, [Item(ReapplyAction.Move)]));

        Assert.StartsWith("統合設定の再適用 実行:", lines[0]);
        Assert.StartsWith("  移動 TBSラジオ", lines[1]);
    }
}
