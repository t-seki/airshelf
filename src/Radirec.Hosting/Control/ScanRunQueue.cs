using System.Collections.Concurrent;
using Radirec.Core.Ingestion;

namespace Radirec.Hosting.Control;

/// <summary>
/// 走査の実行(CONTEXT.md。ADR-0020 / #180)の待ち行列。頼む側(制御チャネル)が <see cref="Enqueue"/> で積み、常駐の直列ループが
/// <see cref="RunPendingAsync"/> で処理する。頼む側は走査を自分で走らせないので、走査は取り込みの走査・再適用と同じループの上で
/// 1 つずつ走り、取り込み中に頼んだら、その走査が終わってから始まる。
/// <para>
/// 結果を返す非冪等の要求なので、トリガのチャネル(容量 1・満杯なら書き捨て)には乗せず、取りこぼさないキューで運ぶ
/// (再適用のジョブと同じ理屈)。ループを起こすのは頼む側の責任(トリガのチャネルへの書き込み)。
/// </para>
/// </summary>
/// <param name="summaryGate">走査の実行の間だけ新規取込のトーストを止める門。取り込みグラフに渡した publisher と同じもの。</param>
public sealed class ScanRunQueue(ScanRunSummaryGate summaryGate)
{
    private readonly ConcurrentQueue<TaskCompletionSource<ScanRunResult>> _pending = new();

    /// <summary>走査の実行を積む。返した Task は、直列ループが走査を終えたとき(または中止したとき)に完了する。</summary>
    public Task<ScanRunResult> Enqueue()
    {
        // 待っているのは制御チャネルの受け口。ループのスレッドで続きを走らせない。
        var completion = new TaskCompletionSource<ScanRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Enqueue(completion);
        return completion.Task;
    }

    /// <summary>
    /// 積まれている走査の実行を処理する(<see cref="TakePending"/> → <see cref="RunAsync"/>)。無ければ何もせず false を返す。
    /// </summary>
    public async Task<bool> RunPendingAsync(Func<CancellationToken, Task<IngestReport>> scan, CancellationToken cancellationToken)
    {
        if (TakePending() is not { } batch)
            return false;
        await RunAsync(batch, scan, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// その時点までに積まれた走査の実行をまとめて取り出す(直列ループの上で呼ぶ)。無ければ null。取り出した後に積まれた分は、
    /// この回には入らず次に回る。直列ループは、取り出した**後**に保留中の設定リロードを反映してから <see cref="RunAsync"/> を呼ぶ
    /// (頼む側は「設定リロードを置く → 走査の実行を積む」の順なので、取り出せた走査の実行の前に置かれた設定リロードは必ず見える。
    /// 再適用のジョブと同じ理屈)。
    /// </summary>
    public ScanRunBatch? TakePending()
    {
        var jobs = new List<TaskCompletionSource<ScanRunResult>>();
        while (_pending.TryDequeue(out var job))
            jobs.Add(job);
        return jobs.Count == 0 ? null : new ScanRunBatch(jobs);
    }

    /// <summary>
    /// 取り出した走査の実行を、<paramref name="scan"/> で走査を 1 回だけ行って、同じ結果で全部を完了させる(どれもこの走査より前に
    /// 頼まれているので、頼んだ時点の録音はすべてこの走査で見る)。走査の間は新規取込のトーストを止める。
    /// <para>
    /// 走査の例外は結果にして返す(ループへは漏らさない)。停止要求(<paramref name="cancellationToken"/> による
    /// <see cref="OperationCanceledException"/>)だけは中止として完了させてからループへ伝える。停止要求でない
    /// <see cref="OperationCanceledException"/>(呼んだ先の独自の時間切れなど)は内部の失敗として結果にする。
    /// </para>
    /// </summary>
    public async Task RunAsync(
        ScanRunBatch batch, Func<CancellationToken, Task<IngestReport>> scan, CancellationToken cancellationToken)
    {
        var jobs = batch.Jobs;
        ScanRunResult result;
        try
        {
            IngestReport report;
            using (summaryGate.Suppress())
                report = await scan(cancellationToken).ConfigureAwait(false);
            result = ScanRunResult.Ok(ScanRunReport.From(report));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Complete(jobs, ScanRunResult.Failed(ScanRunFailure.Stopped, StoppedMessage));
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            result = ScanRunResult.Failed(ScanRunFailure.External, $"走査に失敗しました: {ex.Message}");
        }
        catch (Exception ex)
        {
            result = ScanRunResult.Failed(ScanRunFailure.Internal, $"走査に失敗しました: {ex.Message}");
        }

        Complete(jobs, result);
    }

    /// <summary>残っている走査の実行を中止として完了させる(ループが抜けた後。頼んだ側が永久に待たないように)。</summary>
    public void FailPending()
    {
        while (_pending.TryDequeue(out var job))
            job.TrySetResult(ScanRunResult.Failed(ScanRunFailure.Stopped, StoppedMessage));
    }

    /// <summary>取り込みが止まったために走査しなかった・中止したときの理由。</summary>
    public const string StoppedMessage = "取り込みが停止したため走査を中止しました";

    private static void Complete(IReadOnlyList<TaskCompletionSource<ScanRunResult>> jobs, ScanRunResult result)
    {
        foreach (var job in jobs)
            job.TrySetResult(result);
    }
}

/// <summary><see cref="ScanRunQueue.TakePending"/> で取り出した、1 回の走査で処理する走査の実行のまとまり。</summary>
public sealed class ScanRunBatch
{
    internal ScanRunBatch(IReadOnlyList<TaskCompletionSource<ScanRunResult>> jobs) => Jobs = jobs;

    internal IReadOnlyList<TaskCompletionSource<ScanRunResult>> Jobs { get; }

    /// <summary>まとめた走査の実行の数。</summary>
    public int Count => Jobs.Count;
}
