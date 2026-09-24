using Radirec.Core.Ingestion;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;
using Radirec.Gui.Core;

namespace Radirec.Tests;

/// <summary>トレイからの再適用(#126)で出すダイアログの文言と gui.log の明細行。</summary>
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
    public void 揃っていても統合設定の無い兄弟フォルダは出す()
    {
        var plan = Report(true, [], [Sibling("番組X", "番組X(2)")]);

        var text = ReapplyDialogFormatter.BuildAligned(plan);

        Assert.Contains("統合設定の無い兄弟フォルダ", text.Body);
        Assert.Contains("・TBSラジオ: 番組X(1) / 番組X(2)(設定なし)", text.Body);
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

    [Fact]
    public void 兄弟フォルダも上限を超えたらほかに畳む()
    {
        var groups = Enumerable.Range(1, ReapplyDialogFormatter.MaxSiblingGroups + 2)
            .Select(i => Sibling($"番組{i}", $"番組{i}(1)"))
            .ToList();

        var text = ReapplyDialogFormatter.BuildConfirmation(Report(true, [Item(ReapplyAction.Move)], groups));

        Assert.Contains("…ほか 2 組(gui.log 参照)", text.Body);
        Assert.DoesNotContain($"番組{ReapplyDialogFormatter.MaxSiblingGroups + 1}(1)", text.Body);
    }

    // ---- 統合設定の追記(#144)----

    [Fact]
    public void 追記できるときだけ兄弟フォルダの段にボタンの案内を添える()
    {
        var plan = Report(true, [Item(ReapplyAction.Move)], [Sibling("番組", "番組(1)", "番組(2)")]);
        var aligned = Report(true, [], [Sibling("番組", "番組(1)", "番組(2)")]);

        Assert.Contains("[統合設定を追記して開く] で", ReapplyDialogFormatter.BuildConfirmation(plan, canAppend: true).Body);
        Assert.Contains("[統合設定を追記して開く] で", ReapplyDialogFormatter.BuildAligned(aligned, canAppend: true).Body);
        Assert.DoesNotContain("追記", ReapplyDialogFormatter.BuildConfirmation(plan).Body);
        // 兄弟フォルダが無ければ案内も無い
        Assert.DoesNotContain("追記", ReapplyDialogFormatter.BuildConfirmation(Report(true, [Item(ReapplyAction.Move)]), canAppend: true).Body);
    }

    [Fact]
    public void 追記の確認は組の数と中身を組ごとに示し_再適用を選び直すよう案内する()
    {
        SiblingProgramProposal[] proposals =
        [
            new("文化放送", "てるのりのワルノリ", ["てるのりのワルノリ (1)", "てるのりのワルノリ (2)"], JoinsExisting: false),
            new("文化放送", "おとなりさん", ["おとなりさん 11時12時"], JoinsExisting: true),
        ];

        var text = ReapplyDialogFormatter.BuildAppendConfirmation(proposals, "programs.json");

        Assert.Equal("次の 2 組を programs.json に追記して開きます", text.Heading);
        Assert.Contains("・文化放送「てるのりのワルノリ」: 2 フォルダを統合する設定", text.Body);
        Assert.Contains("・文化放送 統合番組『おとなりさん』に足すフォルダ 1 つ(コメントで案内します。既存の設定は変えません)", text.Body);
        Assert.Contains("programs.json.bak-日時", text.Body);
        Assert.Contains("もう一度「統合設定の再適用…」を選んでください", text.Body);
        Assert.Contains("もう一度「統合設定の再適用…」を選んでください", ReapplyDialogFormatter.AppendedBalloon("programs.json"));
    }

    [Fact]
    public void 見本から作ってから追記したときはバルーンでそれも知らせる()
    {
        Assert.StartsWith("programs.json が無かったので見本から作り、統合設定の提案を追記しました。",
            ReapplyDialogFormatter.AppendedBalloon("programs.json", seeded: true));
        Assert.StartsWith("programs.json に統合設定の提案を追記しました。", ReapplyDialogFormatter.AppendedBalloon("programs.json"));

        var failed = ReapplyDialogFormatter.BuildSeedFailed("programs.json");
        Assert.Contains("見本から作ることもできませんでした", failed.Body);
        Assert.DoesNotContain("開きます", failed.Body); // 作れなければ開けないので、開くとは言わない
    }

    [Fact]
    public void 追記する中身は上限の行数で畳む()
    {
        var shortSnippet = "a\nb\n";
        var longSnippet = string.Concat(Enumerable.Range(1, ReapplyDialogFormatter.MaxSnippetLines + 5).Select(i => $"line{i}\n"));

        Assert.Equal("a\nb", ReapplyDialogFormatter.SnippetPreview(shortSnippet));
        var preview = ReapplyDialogFormatter.SnippetPreview(longSnippet);
        Assert.Contains($"line{ReapplyDialogFormatter.MaxSnippetLines}\n", preview);
        Assert.DoesNotContain($"line{ReapplyDialogFormatter.MaxSnippetLines + 1}", preview);
        Assert.EndsWith("…ほか 5 行(追記した後に開くファイルで確かめてください)", preview);
    }

    [Fact]
    public void 追記できなかったときは理由と何も書いていないことを示す()
    {
        var text = ReapplyDialogFormatter.BuildAppendFailed("programs.json", "programs.json を読めません(JSON が壊れています)");

        Assert.Equal("programs.json に追記できませんでした", text.Heading);
        Assert.StartsWith("programs.json を読めません", text.Body);
        Assert.Contains("何も書き込んでいません", text.Body);
    }

    [Fact]
    public void 見本から作ったあとに追記できなかったときは_何も書いていないとは言わない()
    {
        var text = ReapplyDialogFormatter.BuildAppendFailed("programs.json", "programs.json を読めません", seeded: true);

        Assert.Equal("programs.json に追記できませんでした", text.Heading);
        Assert.StartsWith("programs.json を読めません", text.Body);
        Assert.Contains("programs.json が無かったので見本から作りましたが、提案の追記はできませんでした。", text.Body);
        Assert.DoesNotContain("何も書き込んでいません", text.Body);
        Assert.EndsWith("programs.json を開きます。", text.Body);
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
