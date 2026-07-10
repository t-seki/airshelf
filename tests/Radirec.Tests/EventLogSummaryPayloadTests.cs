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
                PartIndex = 2,
            },
        ]);

        using var doc = JsonDocument.Parse(EventLogIngestSummaryPublisher.BuildPayload(summary));
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("totalImported").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());

        var album = Assert.Single(root.GetProperty("albums").EnumerateArray().ToList());
        Assert.Equal("ニッポン放送", album.GetProperty("station").GetString());
        Assert.Equal("オールナイトニッポン0", album.GetProperty("program").GetString());

        var episode = Assert.Single(album.GetProperty("episodes").EnumerateArray().ToList());
        Assert.Equal("2026-07-09", episode.GetProperty("broadcastDate").GetString());
        Assert.Equal(2, episode.GetProperty("partIndex").GetInt32());
    }
}
