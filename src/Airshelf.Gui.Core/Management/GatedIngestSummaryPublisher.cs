using Airshelf.Core.Ingestion;

namespace Airshelf.Gui.Core.Management;

/// <summary>
/// 新規取込のトースト(取込要約シグナルの提示)を、<see cref="IngestToastSetting"/> で止められるようにする(#152)。
/// 止めているときは <paramref name="inner"/> に渡さない。取込失敗シグナル(<see cref="IIngestFailurePublisher"/>)は
/// これを通さないので、この設定に関係なく出る。設定はシグナルを受けるたびに読む(切り替えはすぐ効く)。
/// </summary>
public sealed class GatedIngestSummaryPublisher(IIngestSummaryPublisher inner, Func<bool> enabled) : IIngestSummaryPublisher
{
    /// <summary>止めたときに返す理由。gui.log の走査の行の「通知」の欄に出る(#215)。</summary>
    public const string SuppressedReason = "設定でオフ";

    public Task<string?> PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default) =>
        enabled() ? inner.PublishAsync(summary, cancellationToken) : Task.FromResult<string?>(SuppressedReason);
}
