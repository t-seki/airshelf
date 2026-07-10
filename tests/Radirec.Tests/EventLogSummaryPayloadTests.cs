using System.Globalization;
using System.Text.Json;
using Radirec.Core.Ingestion;
using Radirec.Core.Model;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// 取込要約シグナルの JSON envelope はサービス↔GUI のワイヤ契約(ADR-0008)。
/// フィールド名・schemaVersion・日付書式が崩れると受け手(GUI)の parse が壊れる。
/// </summary>
public sealed class EventLogSummaryPayloadTests
{
    [Fact]
    public void 契約フィールドと日付書式を満たす()
    {
        var summary = IngestSummary.From(
        [
            new Episode
            {
                Identity = new IdentityKey("ニッポン放送", "オールナイトニッポン0", new DateOnly(2026, 7, 9)),
                BroadcastTime = new TimeOnly(3, 0, 0),
                PartIndex = 2,
            },
        ]);

        var json = EventLogIngestSummaryPublisher.BuildPayload(summary);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("totalImported").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());

        var album = Assert.Single(root.GetProperty("albums").EnumerateArray().ToList());
        Assert.Equal("ニッポン放送", album.GetProperty("station").GetString());
        Assert.Equal("オールナイトニッポン0", album.GetProperty("program").GetString());

        var episode = Assert.Single(album.GetProperty("episodes").EnumerateArray().ToList());
        Assert.Equal("2026-07-09", episode.GetProperty("broadcastDate").GetString());
        Assert.Equal("03:00", episode.GetProperty("broadcastTime").GetString());
        Assert.Equal(2, episode.GetProperty("partIndex").GetInt32());

        // 日本語は \uXXXX に escape せず素の 1 文字で出す(EventLog 文字数上限対策)。
        Assert.Contains("ニッポン放送", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void 放送時刻が取得不能ならnull()
    {
        var summary = IngestSummary.From(
        [
            new Episode
            {
                Identity = new IdentityKey("局", "番組", new DateOnly(2026, 1, 1)),
            },
        ]);

        using var doc = JsonDocument.Parse(EventLogIngestSummaryPublisher.BuildPayload(summary));
        var episode = doc.RootElement.GetProperty("albums")[0].GetProperty("episodes")[0];

        Assert.Equal(JsonValueKind.Null, episode.GetProperty("broadcastTime").ValueKind);
    }

    [Fact]
    public void 時刻書式はカルチャ非依存()
    {
        // time separator が ':' でないカルチャ(fi-FI は '.')でも HH:mm を保つ。
        var original = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("fi-FI");
        try
        {
            var summary = IngestSummary.From(
            [
                new Episode
                {
                    Identity = new IdentityKey("局", "番組", new DateOnly(2026, 1, 1)),
                    BroadcastTime = new TimeOnly(3, 0, 0),
                },
            ]);

            using var doc = JsonDocument.Parse(EventLogIngestSummaryPublisher.BuildPayload(summary));
            var episode = doc.RootElement.GetProperty("albums")[0].GetProperty("episodes")[0];

            Assert.Equal("03:00", episode.GetProperty("broadcastTime").GetString());
            Assert.Equal("2026-01-01", episode.GetProperty("broadcastDate").GetString());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
