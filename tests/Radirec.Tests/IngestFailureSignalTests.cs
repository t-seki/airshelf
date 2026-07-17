using System.Text.Json;
using Radirec.Core.Ingestion;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// 取込失敗シグナル(EventID 1002。ADR-0011)の純粋ロジックと JSON 契約。`From` の有界化(表示上限・
/// truncated・出現順)と、発行側 payload のフィールド契約(受け手 GUI が追従する golden)を固定する。
/// </summary>
public sealed class IngestFailureSignalTests
{
    private static NewIngestFailure Fail(string path, string reason = "パースに失敗しました") =>
        new() { SourcePath = path, Fingerprint = new FileFingerprint(10, 20), Reason = reason };

    // ---- From(有界化) ----

    [Fact]
    public void From_ファイル名はパスから取り出す()
    {
        var signal = IngestFailureSignal.From([Fail(@"I:\radio\局\番組\bad_20260717.m4a")]);

        var file = Assert.Single(signal.Files);
        Assert.Equal("bad_20260717.m4a", file.Name); // フルパスは載せない
        Assert.Equal(1, signal.FailedCount);
        Assert.False(signal.Truncated);
    }

    [Fact]
    public void From_出現順を保つ()
    {
        var signal = IngestFailureSignal.From([Fail("/in/先.m4a"), Fail("/in/後.m4a")]);

        Assert.Equal(["先.m4a", "後.m4a"], signal.Files.Select(f => f.Name));
    }

    [Fact]
    public void From_表示上限超過はtruncated_だしFailedCountは全件()
    {
        var many = Enumerable.Range(0, IngestFailureSignal.MaxFiles + 5)
            .Select(i => Fail($"/in/f{i}.m4a")).ToList();

        var signal = IngestFailureSignal.From(many);

        // 表示は上限まで、しかし件数は全件(超過分も de-dup ストアには記録されるので再通知されない)。
        Assert.Equal(IngestFailureSignal.MaxFiles, signal.Files.Count);
        Assert.Equal(IngestFailureSignal.MaxFiles + 5, signal.FailedCount);
        Assert.True(signal.Truncated);
    }

    // ---- 発行側 payload(契約 golden) ----

    [Fact]
    public void BuildPayload_契約フィールドを満たす()
    {
        var signal = IngestFailureSignal.From([Fail("/in/オールナイトニッポン_20260717.m4a", "パースに失敗しました")]);

        var json = EventLogIngestFailurePublisher.BuildPayload(signal);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("failedCount").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());

        var file = Assert.Single(root.GetProperty("files").EnumerateArray().ToList());
        Assert.Equal("オールナイトニッポン_20260717.m4a", file.GetProperty("name").GetString());
        Assert.Equal("パースに失敗しました", file.GetProperty("reason").GetString());
    }

    [Fact]
    public void BuildPayload_CJKを素の文字で出す()
    {
        var signal = IngestFailureSignal.From([Fail("/in/番組.m4a", "局が不明です")]);

        var json = EventLogIngestFailurePublisher.BuildPayload(signal);

        // UnsafeRelaxedJsonEscaping: \uXXXX へ escape せず素の CJK(EventLog 文字数上限に効く)。
        Assert.Contains("局が不明です", json);
        Assert.DoesNotContain("\\u", json);
    }
}
