using Airshelf.Core.Ingestion;
using Airshelf.Core.Ledger;

namespace Airshelf.Tests;

/// <summary>
/// 取込失敗シグナル(ADR-0011)の純粋ロジック。`From` の有界化(表示上限・truncated・出現順・
/// 理由の切り詰め)を固定する。トレイ常駐移行(ADR-0012)で EventLog JSON 契約は退役したため、
/// payload golden テストは除去した(表示は in-process の IngestToastFormatter が担う)。
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

    [Fact]
    public void From_長すぎる理由は切り詰める_payload有界化()
    {
        var longReason = new string('あ', IngestFailureSignal.MaxReasonLength + 100);

        var signal = IngestFailureSignal.From([Fail("/in/a.m4a", longReason)]);

        var reason = signal.Files.Single().Reason;
        Assert.True(reason.Length <= IngestFailureSignal.MaxReasonLength + 1); // + 省略記号
        Assert.EndsWith("…", reason);
    }
}
