using Radirec.Core.Ingestion;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// <see cref="CompositionRoot.SelectPublishers"/> の分岐を検証する。トレイ常駐移行(ADR-0012)後、
/// 取込通知の唯一の経路は注入された in-process publisher(トレイのトースト)。両方注入 + 非 dry-run の
/// ときだけ発行し、それ以外(未注入・notify=false・dry-run)は発行しない。
/// 新規取込のトーストを止める設定(旧 NotificationsEnabled)はここでは見ない(#152・ADR-0019。止めるのは提示側)。
/// 片側だけの注入は ADR-0011 のサイレント抑制を招くため禁止する。
/// </summary>
public class CompositionRootPublisherSelectionTests
{
    private sealed class FakeSummaryPublisher : IIngestSummaryPublisher
    {
        public Task<string?> PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FakeFailurePublisher : IIngestFailurePublisher
    {
        public Task PublishAsync(IngestFailureSignal signal, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    [Theory]
    [InlineData(false, false)] // notify=false
    [InlineData(true, true)]   // dry-run
    public void Gated_off_yields_null_publishers_and_no_store(bool notify, bool dryRun)
    {
        var (summary, failure, useStore) = CompositionRoot.SelectPublishers(
            notify, dryRun, injectedSummary: new FakeSummaryPublisher(), injectedFailure: new FakeFailurePublisher());

        Assert.IsType<NullIngestSummaryPublisher>(summary);
        Assert.IsType<NullIngestFailurePublisher>(failure);
        Assert.False(useStore);
    }

    [Fact]
    public void No_injection_does_not_publish()
    {
        // 移行後は既定の発行経路が無い。注入が無ければ発行しない。
        var (summary, failure, useStore) = CompositionRoot.SelectPublishers(
            notify: true, dryRun: false, injectedSummary: null, injectedFailure: null);

        Assert.IsType<NullIngestSummaryPublisher>(summary);
        Assert.IsType<NullIngestFailurePublisher>(failure);
        Assert.False(useStore);
    }

    [Fact]
    public void Both_injected_publishers_used_and_enable_store()
    {
        var summaryFake = new FakeSummaryPublisher();
        var failureFake = new FakeFailurePublisher();

        var (summary, failure, useStore) = CompositionRoot.SelectPublishers(
            notify: true, dryRun: false, injectedSummary: summaryFake, injectedFailure: failureFake);

        Assert.Same(summaryFake, summary);
        Assert.Same(failureFake, failure);
        Assert.True(useStore);
    }

    [Fact]
    public void Injection_ignored_when_notify_off()
    {
        var summaryFake = new FakeSummaryPublisher();
        var failureFake = new FakeFailurePublisher();

        // notify=false(単発 CLI 経路)では、たとえ注入があっても発行しない。
        var (summary, failure, useStore) = CompositionRoot.SelectPublishers(
            notify: false, dryRun: false, injectedSummary: summaryFake, injectedFailure: failureFake);

        Assert.IsType<NullIngestSummaryPublisher>(summary);
        Assert.IsType<NullIngestFailurePublisher>(failure);
        Assert.False(useStore);
    }

    [Theory]
    [InlineData(true, false)]  // summary のみ注入
    [InlineData(false, true)]  // failure のみ注入
    public void Partial_injection_throws(bool injectSummary, bool injectFailure)
    {
        // 片側だけの注入は ADR-0011 のサイレント抑制を招くため禁止(両方 or どちらも)。
        Assert.Throws<ArgumentException>(() => CompositionRoot.SelectPublishers(
            notify: true, dryRun: false,
            injectedSummary: injectSummary ? new FakeSummaryPublisher() : null,
            injectedFailure: injectFailure ? new FakeFailurePublisher() : null));
    }
}
