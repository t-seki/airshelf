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

/// <summary>
/// 何もしない実装(dry-run / 手動 CLI などの非発行走査 / publisher を注入しない経路)。常駐の失敗の発行は、新規取込の
/// トーストの設定に左右されない(止めても失敗は発行し、失敗のストアにも記録する。#152・ADR-0019)。
/// </summary>
public sealed class NullIngestFailurePublisher : IIngestFailurePublisher
{
    public Task PublishAsync(IngestFailureSignal signal, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
