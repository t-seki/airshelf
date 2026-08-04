namespace Radirec.Core.Ingestion;

/// <summary>
/// 取込失敗シグナルを発行する(成功要約とは別種の独立シグナル。ADR-0011)。提示の手段は実装側に隠す。
/// 発行失敗は呼び出し側で握られ、de-dup 記録を進めない(次走査で再試行 = at-least-once)。
/// </summary>
public interface IIngestFailurePublisher
{
    /// <summary>失敗シグナルを 1 件発行する。</summary>
    Task PublishAsync(IngestFailureSignal signal, CancellationToken cancellationToken = default);
}

/// <summary>何もしない実装(通知無効時 / dry-run / 非 Windows / 非 notify 経路)。</summary>
public sealed class NullIngestFailurePublisher : IIngestFailurePublisher
{
    public Task PublishAsync(IngestFailureSignal signal, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
