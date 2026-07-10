namespace Radirec.Core.Ingestion;

/// <summary>
/// 取込要約シグナルを発行する(EventLog 等の transport は実装側に隠す。ADR-0008)。
/// 発行失敗は呼び出し側で握られ、取り込み成功を巻き戻さない(<c>ILibraryRefresher</c> と同方針)。
/// </summary>
public interface IIngestSummaryPublisher
{
    /// <summary>要約を 1 件発行する。</summary>
    Task PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default);
}

/// <summary>何もしない実装(通知無効時 / dry-run / 非 Windows)。</summary>
public sealed class NullIngestSummaryPublisher : IIngestSummaryPublisher
{
    public Task PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
