namespace Radirec.Core.Ingestion;

/// <summary>
/// 取込要約シグナルを発行する(提示の手段は実装側に隠す。現行の実装はトレイの in-process トースト)。
/// 発行失敗は呼び出し側で握られ、取り込み成功を巻き戻さない(<c>ILibraryRefresher</c> と同方針)。
/// </summary>
public interface IIngestSummaryPublisher
{
    /// <summary>要約を 1 件発行する。</summary>
    Task PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default);
}

/// <summary>
/// 何もしない実装(dry-run / 手動 CLI などの非発行走査 / publisher を注入しない経路)。常駐で新規取込のトーストを
/// 止めているときはこれを使わず、提示側(トレイ)が成功要約のトーストだけを出さない(#152・ADR-0019)。
/// </summary>
public sealed class NullIngestSummaryPublisher : IIngestSummaryPublisher
{
    public Task PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
