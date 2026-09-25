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
    /// 積まれている走査の実行を処理する(直列ループの上で呼ぶ)。無ければ何もせず false を返す。あれば、その時点までに積まれた分を
    /// まとめて取り出し、<paramref name="scan"/> で走査を 1 回だけ行って、同じ結果で全部を完了させる(どれもこの走査より前に
    /// 頼まれているので、頼んだ時点の録音はすべてこの走査で見る)。走査の間は新規取込のトーストを止める。
    /// <para>
    /// 走査の例外は結果にして返す(ループへは漏らさない)。停止要求(<see cref="OperationCanceledException"/>)は中止として完了させてから
    /// ループへ伝える。
    /// </para>
    /// </summary>
    public async Task<bool> RunPendingAsync(Func<CancellationToken, Task<IngestReport>> scan, CancellationToken cancellationToken)
    {
        var jobs = new List<TaskCompletionSource<ScanRunResult>>();
        while (_pending.TryDequeue(out var job))
            jobs.Add(job);
        if (jobs.Count == 0)
            return false;

        ScanRunResult result;
        try
        {
            IngestReport report;
            using (summaryGate.Suppress())
                report = await scan(cancellationToken).ConfigureAwait(false);
            result = ScanRunResult.Ok(ScanRunReport.From(report));
        }
        catch (OperationCanceledException)
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
        return true;
    }

    /// <summary>残っている走査の実行を中止として完了させる(ループが抜けた後。頼んだ側が永久に待たないように)。</summary>
    public void FailPending()
    {
        while (_pending.TryDequeue(out var job))
            job.TrySetResult(ScanRunResult.Failed(ScanRunFailure.Stopped, StoppedMessage));
    }

    /// <summary>取り込みが止まったために走査しなかった・中止したときの理由。</summary>
    public const string StoppedMessage = "取り込みが停止したため走査を中止しました";

    private static void Complete(List<TaskCompletionSource<ScanRunResult>> jobs, ScanRunResult result)
    {
        foreach (var job in jobs)
            job.TrySetResult(result);
    }
}
