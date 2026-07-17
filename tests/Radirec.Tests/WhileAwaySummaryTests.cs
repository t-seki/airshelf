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

    // ---- チェックポイントの永続化往復 ----

    [Fact]
    public void チェックポイントはシリアライズ往復する()
    {
        var original = new SubscriptionCheckpoint { BookmarkXml = "<BookmarkList/>", LastRecordId = 42 };

        var restored = SubscriptionCheckpoint.TryDeserialize(original.Serialize());

        Assert.NotNull(restored);
        Assert.Equal("<BookmarkList/>", restored!.BookmarkXml);
        Assert.Equal(42, restored.LastRecordId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    public void 空や破損はnull_呼び手は縮退する(string? json)
    {
        Assert.Null(SubscriptionCheckpoint.TryDeserialize(json));
    }
}
