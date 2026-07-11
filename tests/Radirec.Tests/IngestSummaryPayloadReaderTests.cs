using Radirec.Core.Ingestion;
using Radirec.Core.Model;
using Radirec.Gui.Core;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// 受け手(GUI)側の JSON 契約 parse。発行側(Hosting)の envelope とドリフトしていないことを
/// round-trip で検証する(ADR-0009)。
/// </summary>
public sealed class IngestSummaryPayloadReaderTests
{
    [Fact]
    public void 発行側envelopeをそのまま解析できる_ドリフト検出()
    {
        // 発行側(Hosting)が実際に出す JSON を受け手 DTO で解析し、フィールドが一致すること。
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

        Assert.True(IngestSummaryPayloadReader.TryParse(json, out var payload, out var error), error);
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.SchemaVersion);
        Assert.Equal(1, payload.TotalImported);
        Assert.False(payload.Truncated);

        var album = Assert.Single(payload.Albums);
        Assert.Equal("ニッポン放送", album.Station);
        Assert.Equal("オールナイトニッポン0", album.Program);

        var episode = Assert.Single(album.Episodes);
        Assert.Equal("2026-07-09", episode.BroadcastDate);
        Assert.Equal("03:00", episode.BroadcastTime);
        Assert.Equal(2, episode.PartIndex);
    }

    [Fact]
    public void 放送時刻nullも解析できる()
    {
        var summary = IngestSummary.From(
            [new Episode { Identity = new IdentityKey("局", "番組", new DateOnly(2026, 1, 1)) }]);
        var json = EventLogIngestSummaryPublisher.BuildPayload(summary);

        Assert.True(IngestSummaryPayloadReader.TryParse(json, out var payload, out _));
        Assert.Null(payload!.Albums[0].Episodes[0].BroadcastTime);
    }

    [Fact]
    public void 未対応schemaVersionは弾く()
    {
        var json = """{"schemaVersion":999,"totalImported":1,"truncated":false,"albums":[]}""";

        Assert.False(IngestSummaryPayloadReader.TryParse(json, out var payload, out var error));
        Assert.Null(payload);
        Assert.Contains("999", error);
    }

    [Fact]
    public void 壊れたJSONは弾く()
    {
        Assert.False(IngestSummaryPayloadReader.TryParse("{ not json", out var payload, out var error));
        Assert.Null(payload);
        Assert.NotNull(error);
    }

    [Fact]
    public void 未知フィールドは無視して前方互換()
    {
        // 発行側が将来フィールドを足しても(schemaVersion 据え置きの範囲で)壊れない。
        var json = """
        {"schemaVersion":1,"totalImported":1,"truncated":false,"newField":"x",
         "albums":[{"station":"局","program":"番組","extra":1,"episodes":[{"broadcastDate":"2026-01-01","partIndex":0}]}]}
        """;

        Assert.True(IngestSummaryPayloadReader.TryParse(json, out var payload, out var error), error);
        Assert.Equal("番組", payload!.Albums[0].Program);
    }
}
