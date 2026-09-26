namespace Radirec.Core.Ingestion;

/// <summary>
/// 取込要約シグナルを発行する(提示の手段は実装側に隠す。現行の実装はトレイの in-process トースト)。
/// 発行失敗は呼び出し側で握られ、取り込み成功を巻き戻さない(<c>ILibraryRefresher</c> と同方針)。
/// </summary>
public interface IIngestSummaryPublisher
{
    /// <summary>
    /// 要約を 1 件発行する。
    /// </summary>
    /// <returns>
    /// 出さなかったときはその理由(例:<c>CLI の走査</c>・<c>設定でオフ</c>)、出したときは null。gui.log の走査の行の
    /// 「通知」の欄に残し、トーストが出ない理由を調べられるようにする(#215)。
    /// </returns>
    Task<string?> PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default);
}

/// <summary>
/// 何もしない実装(dry-run / publisher を注入しない経路)。常駐で新規取込のトーストを止めているときはこれを使わず、
/// <c>GatedIngestSummaryPublisher</c> が成功要約のトーストだけを出さない(#152・ADR-0019)。走査の実行(CLI から頼んだ
/// 走査。#180)も常駐の publisher を通る(<c>ScanRunSummaryGate</c>)。これは理由を返さない(提示の相手が居ない経路で、
/// 結果は gui.log に書かれない)。
/// </summary>
public sealed class NullIngestSummaryPublisher : IIngestSummaryPublisher
{
    public Task<string?> PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}
