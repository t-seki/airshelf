using CoreIngestion = Radirec.Core.Ingestion;
using Radirec.Core.Ledger;
using Radirec.Gui.Core;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// 受け手(Gui.Core)側の取込失敗シグナル(1002。ADR-0011)の契約 parse・不在中マージ・トースト整形。
/// 発行側(Hosting)の実 payload を受け手 DTO で解析し、ドリフトを round-trip で検出する。EventLog 実機経路は
/// 範囲外(Windows 実機の振る舞いチェックリストで検証。#59)。
/// </summary>
public sealed class IngestFailureReceiverTests
{
    private static string PublisherPayload(params (string Name, string Reason)[] files)
    {
        var news = files
            .Select(f => new CoreIngestion.NewIngestFailure
            {
                SourcePath = @"I:\radio\" + f.Name,
                Fingerprint = new FileFingerprint(1, 2),
                Reason = f.Reason,
            })
            .ToList();
        return EventLogIngestFailurePublisher.BuildPayload(CoreIngestion.IngestFailureSignal.From(news));
    }

    // ---- 契約 parse(発行側 ↔ 受け手 DTO の round-trip) ----

    [Fact]
    public void 発行側の1002payloadを受け手DTOで解析できる_ドリフト検出()
    {
        var json = PublisherPayload(("[X局]番組(TimeFree)_20250229123000.m4a", "パースに失敗しました"));

        Assert.True(IngestFailurePayloadReader.TryParse(json, out var payload, out var error), error);
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.FailedCount);
        Assert.False(payload.Truncated);
        var file = Assert.Single(payload.Files);
        Assert.Equal("[X局]番組(TimeFree)_20250229123000.m4a", file.Name);
        Assert.Equal("パースに失敗しました", file.Reason);
    }

    [Fact]
    public void 未対応schemaVersionは弾く()
    {
        const string future = """{"schemaVersion":99,"failedCount":1,"truncated":false,"files":[]}""";

        Assert.False(IngestFailurePayloadReader.TryParse(future, out _, out var error));
        Assert.Contains("schemaVersion", error);
    }

    // ---- 不在中マージ ----

    [Fact]
    public void 不在中の失敗は合計され_ファイルは連結される()
    {
        var a = Parse(PublisherPayload(("a.m4a", "理由A")));
        var b = Parse(PublisherPayload(("b.m4a", "理由B"), ("c.m4a", "理由C")));

        var merged = WhileAwayFailures.Merge([a, b]);

        Assert.Equal(3, merged.FailedCount);
        Assert.Equal(["a.m4a", "b.m4a", "c.m4a"], merged.Files.Select(f => f.Name));
    }

    [Fact]
    public void 不在中マージは表示上限でtruncated()
    {
        var signals = Enumerable.Range(0, WhileAwayFailures.MaxFiles + 3)
            .Select(i => new IngestFailurePayload
            {
                SchemaVersion = 1,
                FailedCount = 1,
                Files = [new IngestFailureFilePayload { Name = $"f{i}.m4a", Reason = "x" }],
            })
            .ToList();

        var merged = WhileAwayFailures.Merge(signals);

        Assert.Equal(WhileAwayFailures.MaxFiles + 3, merged.FailedCount); // 件数は全件
        Assert.Equal(WhileAwayFailures.MaxFiles, merged.Files.Count);      // 表示は上限
        Assert.True(merged.Truncated);
    }

    // ---- トースト整形 ----

    [Fact]
    public void ライブ失敗トーストは表題と理由行を出す()
    {
        var payload = Parse(PublisherPayload(("番組.m4a", "局が不明です")));

        var texts = IngestToastFormatter.BuildFailureTexts(payload);

        Assert.Equal("取込失敗 1件", texts[0]);
        Assert.Contains("番組.m4a（局が不明です）", texts);
    }

    [Fact]
    public void 不在中失敗トーストは別表題()
    {
        var payload = Parse(PublisherPayload(("a.m4a", "x"), ("b.m4a", "y"), ("c.m4a", "z")));

        var texts = IngestToastFormatter.BuildWhileAwayFailureTexts(payload);

        Assert.Equal("不在中の失敗 3件", texts[0]);
        Assert.Contains("…ほか", texts);                       // 3 要素上限で溢れる
        Assert.True(texts.Count <= IngestToastFormatter.MaxToastTextElements);
    }

    private static IngestFailurePayload Parse(string json)
    {
        Assert.True(IngestFailurePayloadReader.TryParse(json, out var payload, out var error), error);
        return payload!;
    }
}
