using Radirec.Core.Ingestion;
using Radirec.Core.Ledger;
using Radirec.Hosting.Control;

namespace Radirec.Tests;

/// <summary>
/// 走査の実行(CLI から頼んだ走査。#180 / ADR-0020)の待ち行列。頼む側は走査を自分で走らせず、常駐の直列ループが
/// <see cref="ScanRunQueue.RunPendingAsync"/> で処理する(トーストの扱いは <see cref="IngestServiceTests"/>)。
/// </summary>
public sealed class ScanRunQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static IngestReport Report(params IngestOutcome[] outcomes) => new()
    {
        DryRun = false,
        Items = outcomes.Select((o, i) => new IngestItemResult
        {
            Outcome = o,
            SourcePath = $"/in/{i}.m4a",
            Fingerprint = new(1, 0),
        }).ToList(),
    };

    private static ScanRunQueue NewQueue() => new(new ScanRunSummaryGate(new NullIngestSummaryPublisher()));

    [Fact]
    public async Task 取り込み中に頼んだ走査の実行は_その走査が終わってから走り_終わるまで結果を返さない()
    {
        var queue = NewQueue();
        var order = new List<string>();
        var triggerScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var triggerScanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 常駐の直列ループと同じ順:トリガの走査(取り込み中)→ 次の反復で走査の実行。
        var loop = Task.Run(async () =>
        {
            triggerScanStarted.SetResult();
            await triggerScan.Task;
            lock (order) order.Add("トリガの走査が終わった");
            await queue.RunPendingAsync(_ =>
            {
                lock (order) order.Add("走査の実行");
                return Task.FromResult(Report(IngestOutcome.Imported));
            }, CancellationToken.None);
        });

        await triggerScanStarted.Task.WaitAsync(Wait);
        var pending = queue.Enqueue();
        await Task.Delay(100);
        Assert.False(pending.IsCompleted); // 取り込み中は待つ
        lock (order) Assert.Empty(order);  // 頼んだ側では走査しない

        triggerScan.SetResult();
        var result = await pending.WaitAsync(Wait);
        await loop.WaitAsync(Wait);

        Assert.Equal(["トリガの走査が終わった", "走査の実行"], order);
        Assert.Equal(1, result.Report!.Count(IngestOutcome.Imported));
    }

    [Fact]
    public async Task 積まれた走査の実行はまとめて1回の走査で同じ結果を返す()
    {
        var queue = NewQueue();
        var first = queue.Enqueue();
        var second = queue.Enqueue();
        var scans = 0;

        var ran = await queue.RunPendingAsync(_ =>
        {
            scans++;
            return Task.FromResult(Report(IngestOutcome.Imported, IngestOutcome.Failed));
        }, CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(1, scans);
        Assert.Same((await first).Report, (await second).Report);
        Assert.Equal(1, (await first).Report!.Count(IngestOutcome.Failed));
    }

    [Fact]
    public async Task 積まれていなければ走査しない()
    {
        var scans = 0;

        var ran = await NewQueue().RunPendingAsync(_ =>
        {
            scans++;
            return Task.FromResult(Report());
        }, CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(0, scans);
    }

    [Fact]
    public async Task 取込元に届かない失敗は外部の失敗_それ以外は内部の失敗として返す()
    {
        var queue = NewQueue();
        var external = queue.Enqueue();
        await queue.RunPendingAsync(_ => throw new IOException("NAS に届きません"), CancellationToken.None);

        var internalFailure = queue.Enqueue();
        await queue.RunPendingAsync(_ => throw new InvalidOperationException("想定外"), CancellationToken.None);

        Assert.Equal(ScanRunFailure.External, (await external).Failure);
        Assert.Contains("NAS に届きません", (await external).Error);
        Assert.Null((await external).Report);
        Assert.Equal(ScanRunFailure.Internal, (await internalFailure).Failure);
    }

    [Fact]
    public async Task 停止要求なら中止として返してからループへ伝える()
    {
        var queue = NewQueue();
        var pending = queue.Enqueue();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            queue.RunPendingAsync(_ => throw new OperationCanceledException(), CancellationToken.None));

        Assert.Equal(ScanRunFailure.Stopped, (await pending).Failure);
    }

    [Fact]
    public async Task ループが抜けた後に残った走査の実行は中止にする()
    {
        var queue = NewQueue();
        var pending = queue.Enqueue();

        queue.FailPending();

        var result = await pending.WaitAsync(Wait);
        Assert.Equal(ScanRunFailure.Stopped, result.Failure);
        Assert.Equal(ScanRunQueue.StoppedMessage, result.Error);
    }
}
