using Radirec.Core.Ingestion;
using Radirec.Hosting;

namespace Radirec.Tests;

/// <summary>
/// <see cref="CompositionRoot.SelectPublishers"/> の分岐(注入 / EventLog 既定 / 非発行)を検証する。
/// トレイ常駐(ADR-0012)の in-process publisher 注入経路と、既存のサービス経路 EventLog、
/// 非 Windows での非発行が正しく分かれることを保証する。
/// </summary>
public class CompositionRootPublisherSelectionTests
{
    private sealed class FakeSummaryPublisher : IIngestSummaryPublisher
    {
        public Task PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    [Theory]
    [InlineData(false, false, true, true)]   // notify=false
    [InlineData(true, true, true, true)]     // dry-run
    [InlineData(true, false, false, true)]   // 通知無効
    public void Gated_off_yields_null_publishers_and_no_store(
        bool notify, bool dryRun, bool notificationsEnabled, bool isWindows)
    {
        var (summary, failure, useStore) = CompositionRoot.SelectPublishers(
            notify, dryRun, notificationsEnabled, isWindows, injectedSummary: null, injectedFailure: null);

        Assert.IsType<NullIngestSummaryPublisher>(summary);
        Assert.IsType<NullIngestFailurePublisher>(failure);
        Assert.False(useStore);
    }

    [Fact]
    public void Windows_without_injection_falls_back_to_eventlog()
    {
        var (summary, failure, useStore) = CompositionRoot.SelectPublishers(
            notify: true, dryRun: false, notificationsEnabled: true, isWindows: true,
            injectedSummary: null, injectedFailure: null);

        Assert.IsType<EventLogIngestSummaryPublisher>(summary);
        Assert.IsType<EventLogIngestFailurePublisher>(failure);
        Assert.True(useStore);
    }

    [Fact]
    public void NonWindows_without_injection_does_not_publish()
    {
        var (summary, failure, useStore) = CompositionRoot.SelectPublishers(
            notify: true, dryRun: false, notificationsEnabled: true, isWindows: false,
            injectedSummary: null, injectedFailure: null);

        Assert.IsType<NullIngestSummaryPublisher>(summary);
        Assert.IsType<NullIngestFailurePublisher>(failure);
        Assert.False(useStore);
    }

    [Fact]
    public void Injected_publisher_is_used_regardless_of_platform_and_enables_store()
    {
        var fake = new FakeSummaryPublisher();

        // 非 Windows でも注入経路は発行する(in-process トーストはプラットフォーム非依存)。
        var (summary, failure, useStore) = CompositionRoot.SelectPublishers(
            notify: true, dryRun: false, notificationsEnabled: true, isWindows: false,
            injectedSummary: fake, injectedFailure: null);

        Assert.Same(fake, summary);
        Assert.IsType<NullIngestFailurePublisher>(failure); // 片側だけ注入 → 欠けた側は Null
        Assert.True(useStore);
    }
}
