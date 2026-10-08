using Airshelf.Core.Ingestion;
using Airshelf.Core.Ledger;
using Airshelf.Hosting.Control;

namespace Airshelf.Tests;

/// <summary>
/// 走査の実行(CLI から頼んだ走査。#180 / ADR-0020)の待ち行列。頼む側は走査を自分で走らせず、常駐の直列ループが
/// <see cref="ScanRunQueue.TakePending"/> で取り出して <see cref="ScanRunQueue.RunAsync"/> で走査する。テストもワーカーと同じ
/// 取り出し → 走査の形で呼ぶ(トーストの扱いは <see cref="IngestServiceTests"/>)。
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

        // 常駐の直列ループと同じ順:トリガの走査(取り込み中)→ 次の反復で取り出して走査の実行。
        var loop = Task.Run(async () =>
        {
            triggerScanStarted.SetResult();
            await triggerScan.Task;
            lock (order) order.Add("トリガの走査が終わった");
            var batch = queue.TakePending();
            Assert.NotNull(batch);
            await queue.RunAsync(batch, _ =>
            {
                lock (order) order.Add("走査の実行");
                return Task.FromResult(Report(IngestOutcome.Imported));
            }, CancellationToken.None);
        }, TestContext.Current.CancellationToken);

        await triggerScanStarted.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        var pending = queue.Enqueue();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted); // 取り込み中は待つ
        lock (order) Assert.Empty(order);  // 頼んだ側では走査しない

        triggerScan.SetResult();
        var result = await pending.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await loop.WaitAsync(Wait, TestContext.Current.CancellationToken);

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

        var batch = queue.TakePending()!;
        await queue.RunAsync(batch, _ =>
        {
            scans++;
            return Task.FromResult(Report(IngestOutcome.Imported, IngestOutcome.Failed));
        }, CancellationToken.None);

        Assert.Equal(2, batch.Count);
        Assert.Equal(1, scans);
        Assert.Same((await first).Report, (await second).Report);
        Assert.Equal(1, (await first).Report!.Count(IngestOutcome.Failed));
    }

    [Fact]
    public void 積まれていなければ取り出すものは無い()
    {
        Assert.Null(NewQueue().TakePending());
    }

    [Fact]
    public async Task 取込元に届かない失敗は外部の失敗_それ以外は内部の失敗として返す()
    {
        var queue = NewQueue();
        var external = queue.Enqueue();
        await queue.RunAsync(queue.TakePending()!, _ => throw new IOException("NAS に届きません"), CancellationToken.None);

        var internalFailure = queue.Enqueue();
        await queue.RunAsync(queue.TakePending()!, _ => throw new InvalidOperationException("想定外"), CancellationToken.None);

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
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queue.RunAsync(queue.TakePending()!, ct => Task.FromCanceled<IngestReport>(ct), stopping.Token));

        Assert.Equal(ScanRunFailure.Stopped, (await pending).Failure);
    }

    [Fact]
    public async Task 停止要求でない取り消しは内部の失敗として返し_ループへは伝えない()
    {
        // 呼んだ先の独自の時間切れ(TaskCanceledException)など。停止として投げ直すと、直列ループがログも残さず終わる。
        var queue = NewQueue();
        var pending = queue.Enqueue();

        await queue.RunAsync(queue.TakePending()!, _ => throw new TaskCanceledException("時間切れ"), CancellationToken.None);

        var result = await pending;
        Assert.Equal(ScanRunFailure.Internal, result.Failure);
        Assert.Contains("時間切れ", result.Error);
    }

    [Fact]
    public async Task 取り出した後に積まれた走査の実行は_その回の走査に入らず次に回る()
    {
        // 直列ループは取り出した後に保留中の設定リロードを反映する。取り出した後に積まれた分をその回で処理すると、
        // その分の前に置かれた設定リロードを反映しないまま走査してしまう。
        var queue = NewQueue();
        var first = queue.Enqueue();
        var batch = queue.TakePending()!;
        var late = queue.Enqueue();

        await queue.RunAsync(batch, _ => Task.FromResult(Report(IngestOutcome.Imported)), CancellationToken.None);

        Assert.Equal(1, batch.Count);
        Assert.True(first.IsCompleted);
        Assert.False(late.IsCompleted);

        var next = queue.TakePending()!;
        await queue.RunAsync(next, _ => Task.FromResult(Report()), CancellationToken.None);
        Assert.Equal(1, next.Count);
        Assert.Empty((await late).Report!.Items);
        Assert.Null(queue.TakePending());
    }

    [Fact]
    public async Task ループが抜けた後に残った走査の実行は中止にする()
    {
        var queue = NewQueue();
        var pending = queue.Enqueue();

        queue.FailPending();

        var result = await pending.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(ScanRunFailure.Stopped, result.Failure);
        Assert.Equal(ScanRunQueue.StoppedMessage, result.Error);
    }
}
