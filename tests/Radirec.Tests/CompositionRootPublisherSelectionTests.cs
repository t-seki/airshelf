using Radirec.Core.Ingestion;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// <see cref="CompositionRoot.SelectPublishers"/> の分岐を検証する。トレイ常駐移行(ADR-0012)後、
/// 取込通知の唯一の経路は注入された in-process publisher(トレイのトースト)。両方注入 + 通知有効 +
/// 非 dry-run のときだけ発行し、それ以外(未注入・notify=false・dry-run・通知無効)は発行しない。
/// 片側だけの注入は ADR-0011 のサイレント抑制を招くため禁止する。
/// </summary>
public class CompositionRootPublisherSelectionTests
{
    private sealed class FakeSummaryPublisher : IIngestSummaryPublisher
    {
        public Task PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeFailurePublisher : IIngestFailurePublisher
    {
        public Task PublishAsync(IngestFailureSignal signal, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    [Theory]
    [InlineData(false, false, true)]  // notify=false
    [InlineData(true, true, true)]    // dry-run
    [InlineData(true, false, false)]  // 通知無効
    public void Gated_off_yields_null_publishers_and_no_store(
        bool notify, bool dryRun, bool notificationsEnabled)
    {
        var (summary, failure, useStore) = CompositionRoot.SelectPublishers(
            notify, dryRun, notificationsEnabled, injectedSummary: null, injectedFailure: null);

        Assert.IsType<NullIngestSummaryPublisher>(summary);
        Assert.IsType<NullIngestFailurePublisher>(failure);
        Assert.False(useStore);
    }

    [Fact]
    public void No_injection_does_not_publish_even_when_enabled()
    {
        // 移行後は既定の EventLog 経路が無い。注入が無ければ通知有効でも発行しない。
        var (summary, failure, useStore) = CompositionRoot.SelectPublishers(
            notify: true, dryRun: false, notificationsEnabled: true,
            injectedSummary: null, injectedFailure: null);

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
            notify: true, dryRun: false, notificationsEnabled: true,
            injectedSummary: summaryFake, injectedFailure: failureFake);

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
            notify: false, dryRun: false, notificationsEnabled: true,
            injectedSummary: summaryFake, injectedFailure: failureFake);

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
            notify: true, dryRun: false, notificationsEnabled: true,
            injectedSummary: injectSummary ? new FakeSummaryPublisher() : null,
            injectedFailure: injectFailure ? new FakeFailurePublisher() : null));
    }
}
