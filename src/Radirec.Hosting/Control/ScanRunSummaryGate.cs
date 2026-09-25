using Radirec.Core.Ingestion;

namespace Radirec.Hosting.Control;

/// <summary>
/// 走査の実行(CLI から頼んだ走査。ADR-0020 / #180)の間だけ、新規取込の取込要約シグナルを <paramref name="inner"/> に渡さない。
/// 結果は頼んだ側(CLI)に返るので、トーストは出さない。取込失敗シグナル(<see cref="IIngestFailurePublisher"/>)はこれを通さないので、
/// 走査の実行でも失敗のトーストは出て、失敗のストアにも記録される(#152 / ADR-0019 と同じく、止めるのは成功要約だけ)。
/// <para>
/// 止めるのは <see cref="Suppress"/> の中から呼んだ走査だけ(<see cref="AsyncLocal{T}"/>)。同じ publisher を使うほかの走査
/// (検知・定期など)には影響しない。
/// </para>
/// </summary>
public sealed class ScanRunSummaryGate(IIngestSummaryPublisher inner) : IIngestSummaryPublisher
{
    private readonly AsyncLocal<bool> _suppressed = new();

    public Task PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default) =>
        _suppressed.Value ? Task.CompletedTask : inner.PublishAsync(summary, cancellationToken);

    /// <summary>
    /// この中で始めた走査の取込要約を止める。止めるのは、これを呼んだ非同期メソッドの中(と、そこから呼んだ処理)だけで、
    /// 返した値を Dispose すると元に戻る。
    /// </summary>
    public IDisposable Suppress()
    {
        _suppressed.Value = true;
        return new Scope(this);
    }

    private sealed class Scope(ScanRunSummaryGate gate) : IDisposable
    {
        public void Dispose() => gate._suppressed.Value = false;
    }
}
