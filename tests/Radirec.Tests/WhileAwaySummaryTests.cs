using Radirec.Gui.Core;

namespace Radirec.Tests;

/// <summary>
/// 不在中サマリ(軸A A2)の純粋ロジック検証。マージ(合計・(局,番組)統合・Truncated OR)、
/// 表題の差し替え、単調ガード(SubscriptionCheckpoint.IsNewer)。EventLog 実機経路はこの範囲外
/// (Windows 実機の振る舞いチェックリストで検証。#54)。
/// </summary>
public sealed class WhileAwaySummaryTests
{
    private static IngestSummaryPayload Payload(
        int total, bool truncated, params IngestSummaryAlbumPayload[] albums) => new()
    {
        SchemaVersion = 1,
        TotalImported = total,
        Truncated = truncated,
        Albums = albums,
    };

    private static IngestSummaryAlbumPayload Album(
        string station, string program, params IngestSummaryEpisodePayload[] episodes) =>
        new() { Station = station, Program = program, Episodes = episodes };

    private static IngestSummaryEpisodePayload Ep(string date, string? time = null, int part = 0) =>
        new() { BroadcastDate = date, BroadcastTime = time, PartIndex = part };

    // ---- マージ ----

    [Fact]
    public void TotalImportedは各シグナルの合計()
    {
        var merged = WhileAwaySummary.Merge([
            Payload(3, false, Album("J-WAVE", "番組A", Ep("2026-07-10"))),
            Payload(2, false, Album("TOKYO FM", "番組B", Ep("2026-07-11"))),
        ]);

        Assert.Equal(5, merged.TotalImported);
    }

    [Fact]
    public void 同一番組は1アルバムへ統合し放送回を連結する()
    {
        var merged = WhileAwaySummary.Merge([
            Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-10", "22:30"))),
            Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-11", "22:30"))),
        ]);

        var album = Assert.Single(merged.Albums);
        Assert.Equal("番組A", album.Program);
        Assert.Equal("J-WAVE", album.Station);
        Assert.Equal(2, album.Episodes.Count);
        Assert.Equal("2026-07-10", album.Episodes[0].BroadcastDate);
        Assert.Equal("2026-07-11", album.Episodes[1].BroadcastDate);
    }

    [Fact]
    public void 局が違えば別番組扱いで統合しない()
    {
        var merged = WhileAwaySummary.Merge([
            Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-10"))),
            Payload(1, false, Album("TOKYO FM", "番組A", Ep("2026-07-10"))),
        ]);

        Assert.Equal(2, merged.Albums.Count);
    }

    [Fact]
    public void アルバムと放送回は出現順を保つ()
    {
        var merged = WhileAwaySummary.Merge([
            Payload(1, false, Album("A局", "先番組", Ep("2026-07-10"))),
            Payload(1, false, Album("B局", "後番組", Ep("2026-07-11"))),
        ]);

        Assert.Equal("先番組", merged.Albums[0].Program);
        Assert.Equal("後番組", merged.Albums[1].Program);
    }

    [Fact]
    public void Truncatedはいずれか真ならOR結合で真()
    {
        var merged = WhileAwaySummary.Merge([
            Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-10"))),
            Payload(1, true, Album("TOKYO FM", "番組B", Ep("2026-07-11"))),
        ]);

        Assert.True(merged.Truncated);
    }

    [Fact]
    public void 空入力は合計0の空サマリ()
    {
        var merged = WhileAwaySummary.Merge([]);

        Assert.Equal(0, merged.TotalImported);
        Assert.False(merged.Truncated);
        Assert.Empty(merged.Albums);
        Assert.Equal(NotificationContract.SupportedSchemaVersion, merged.SchemaVersion);
    }

    [Fact]
    public void マージ結果は不在中の表題でトースト化できる()
    {
        var merged = WhileAwaySummary.Merge([
            Payload(3, false, Album("J-WAVE", "番組A", Ep("2026-07-10", "22:30"))),
            Payload(2, false, Album("J-WAVE", "番組A", Ep("2026-07-11", "22:30"))),
        ]);

        var texts = IngestToastFormatter.BuildWhileAwayTexts(merged);

        Assert.Equal("不在中の取込 5件", texts[0]);
        // 同一番組が統合され「2件」の集約行になる。
        Assert.Contains("番組A（J-WAVE）2件", texts);
    }

    // ---- 単調ガード ----

    [Fact]
    public void 未記録チェックポイントは全レコードを新規扱い()
    {
        var checkpoint = new SubscriptionCheckpoint();
        Assert.True(checkpoint.IsNewer(1));
        Assert.True(checkpoint.IsNewer(long.MaxValue));
    }

    [Theory]
    [InlineData(100, 100, false)] // 提示済み ID そのもの → 新規でない(境界の二重計上を防ぐ)
    [InlineData(100, 99, false)]  // 過去
    [InlineData(100, 101, true)]  // 新規
    public void 提示済みID以下は新規でない(long last, long candidate, bool expected)
    {
        var checkpoint = new SubscriptionCheckpoint { LastRecordId = last };
        Assert.Equal(expected, checkpoint.IsNewer(candidate));
    }

    [Fact]
    public void 単調ガードの静的実装はランタイムと同じ判定()
    {
        // 購読側ランタイムが呼ぶ静的実装。インスタンス版はこれへ委譲する(実物とテストの二重持ち防止)。
        Assert.True(SubscriptionCheckpoint.IsNewer(null, 1));       // 未記録は常に新規
        Assert.False(SubscriptionCheckpoint.IsNewer(100, 100));     // 境界(提示済み ID)は新規でない
        Assert.False(SubscriptionCheckpoint.IsNewer(100, 1));       // ログクリアで巻き戻った ID も新規でない
        Assert.True(SubscriptionCheckpoint.IsNewer(100, 101));
    }

    [Fact]
    public void 同一番組が3シグナルに跨っても放送回を全て統合する()
    {
        // O(n^2) 修正(累積リスト)後も件数が落ちないことの回帰。
        var merged = WhileAwaySummary.Merge([
            Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-10"))),
            Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-11"))),
            Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-12"))),
        ]);

        var album = Assert.Single(merged.Albums);
        Assert.Equal(3, album.Episodes.Count);
        Assert.Equal(["2026-07-10", "2026-07-11", "2026-07-12"],
            album.Episodes.Select(e => e.BroadcastDate));
    }

    // ---- ドレイン判定リデューサ(過去 2 度バグった要のロジック) ----

    private static DrainRecord Rec(long? id, string bookmark, IngestSummaryPayload? parsed = null) =>
        new(id, bookmark, parsed, null);

    private static DrainRecord FailRec(long? id, string bookmark, IngestFailurePayload? parsed) =>
        new(id, bookmark, null, parsed);

    [Fact]
    public void Drain_全件baseline以下ならresumeはnull_縮退する()
    {
        // ログクリアで ID が巻き戻り、全レコードが提示済み baseline 以下になるケース。
        var outcome = WhileAwayDrain.Plan(baseline: 5000, records: [
            Rec(1, "bm1", Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-10")))),
            Rec(2, "bm2", Payload(1, false, Album("TOKYO FM", "番組B", Ep("2026-07-11")))),
        ]);

        Assert.Null(outcome.ResumeBookmarkXml); // → 呼び手は read-from-now へ縮退
        Assert.Null(outcome.MaxRecordId);
        Assert.Empty(outcome.Summaries);
    }

    [Fact]
    public void Drain_一部通過ならresumeとmaxは通過分だけ進む()
    {
        var outcome = WhileAwayDrain.Plan(baseline: 100, records: [
            Rec(101, "bm101", Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-10")))),
            Rec(102, "bm102", Payload(1, false, Album("TOKYO FM", "番組B", Ep("2026-07-11")))),
        ]);

        Assert.Equal("bm102", outcome.ResumeBookmarkXml);
        Assert.Equal(102, outcome.MaxRecordId);
        Assert.Equal(2, outcome.Summaries.Count);
    }

    [Fact]
    public void Drain_スキップの後に通過_resumeは通過分でありnullでない()
    {
        // 境界レコード(baseline と同値 = 再返却)をスキップし、続く新規で resume が確定すること。
        // lastBookmark をガード前に代入していた旧実装ではここが「スキップ分の bookmark」で汚染された。
        var outcome = WhileAwayDrain.Plan(baseline: 100, records: [
            Rec(100, "bm100-boundary"), // 再返却された境界 → スキップ
            Rec(101, "bm101", Payload(2, false, Album("J-WAVE", "番組A", Ep("2026-07-10")))),
        ]);

        Assert.Equal("bm101", outcome.ResumeBookmarkXml);
        Assert.Equal(101, outcome.MaxRecordId);
        Assert.Single(outcome.Summaries);
    }

    [Fact]
    public void Drain_空入力はresumeがnullで縮退()
    {
        var outcome = WhileAwayDrain.Plan(baseline: 100, records: []);

        Assert.Null(outcome.ResumeBookmarkXml);
        Assert.Null(outcome.MaxRecordId);
        Assert.Empty(outcome.Summaries);
    }

    [Fact]
    public void Drain_通過だが解析失敗はcollectedに入らないがresumeは進む()
    {
        // 位置は進める(再スキャンを避ける)が、提示対象には含めない。
        var outcome = WhileAwayDrain.Plan(baseline: null, records: [
            Rec(1, "bm1", parsed: null),
        ]);

        Assert.Equal("bm1", outcome.ResumeBookmarkXml);
        Assert.Equal(1, outcome.MaxRecordId);
        Assert.Empty(outcome.Summaries);
    }

    [Fact]
    public void Drain_成功と失敗をEventIDで別コレクションに振り分ける_単一チェックポイント()
    {
        // 1001(要約)と 1002(失敗)が混在しても、単一チェックポイントで両方通過し、成功・失敗を
        // 別コレクションへ集約する(ADR-0011: 別トーストになる)。
        var failure = new IngestFailurePayload { SchemaVersion = 1, FailedCount = 1, Files = [] };
        var outcome = WhileAwayDrain.Plan(baseline: 100, records: [
            Rec(101, "bm101", Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-10")))),
            FailRec(102, "bm102", failure),
        ]);

        Assert.Single(outcome.Summaries);
        Assert.Single(outcome.Failures);
        Assert.Equal("bm102", outcome.ResumeBookmarkXml); // 最後(失敗)まで位置が進む
        Assert.Equal(102, outcome.MaxRecordId);
    }

    // ---- チェックポイントの永続化往復 ----

    [Fact]
    public void チェックポイントはシリアライズ往復する()
    {
        var original = new SubscriptionCheckpoint { BookmarkXml = "<BookmarkList/>", LastRecordId = 42 };

        Assert.Equal(SubscriptionCheckpoint.CheckpointParse.Loaded,
            SubscriptionCheckpoint.Classify(original.Serialize(), out var restored));
        Assert.NotNull(restored);
        Assert.Equal("<BookmarkList/>", restored!.BookmarkXml);
        Assert.Equal(42, restored.LastRecordId);
    }

    // ---- Classify の 3 値分類(#56: 破損だけ warn するための判別。空/破損の縮退もここで担保) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_空や空白はEmpty_無言(string? json)
    {
        Assert.Equal(SubscriptionCheckpoint.CheckpointParse.Empty,
            SubscriptionCheckpoint.Classify(json, out var cp));
        Assert.Null(cp);
    }

    [Theory]
    [InlineData("{ not json")]  // 壊れた JSON
    [InlineData("null")]        // 非空だが null に化ける
    [InlineData("[1,2,3]")]     // 型違い(配列)
    public void Classify_非空だが解析不能はCorrupt(string json)
    {
        Assert.Equal(SubscriptionCheckpoint.CheckpointParse.Corrupt,
            SubscriptionCheckpoint.Classify(json, out var cp));
        Assert.Null(cp);
    }

    [Fact]
    public void Classify_正常はLoadedで値を返す()
    {
        var json = new SubscriptionCheckpoint { BookmarkXml = "<BookmarkList/>", LastRecordId = 7 }.Serialize();

        Assert.Equal(SubscriptionCheckpoint.CheckpointParse.Loaded,
            SubscriptionCheckpoint.Classify(json, out var cp));
        Assert.NotNull(cp);
        Assert.Equal(7, cp!.LastRecordId);
    }
}
