using System.Threading.Channels;
using Radirec.Hosting.Control;

namespace Radirec.Tests;

/// <summary>
/// 常駐の直列ループの生死(#197)と、停止要求とそれ以外の取り消しの区別(#204)を WSL でも検証する。
/// worker(Radirec.Gui)はこの <see cref="SerialLoop"/> の上で起動処理・ループ・走査・再適用を回す。
/// </summary>
public sealed class SerialLoopTests
{
    [Fact]
    public async Task 起動処理で例外が出ると停止になり_例外は投げ直す()
    {
        var loop = new SerialLoop();
        var stopped = 0;
        var loopRan = false;

        var ex = await Assert.ThrowsAsync<IOException>(() => loop.RunAsync(
            start: () => throw new IOException("NAS 未接続"),
            loop: _ => { loopRan = true; return Task.CompletedTask; },
            onStopped: () => stopped++,
            onUnexpectedExit: _ => Assert.Fail("起動処理の失敗は投げ直す(呼び手が gui.log に残す)"),
            CancellationToken.None));

        Assert.Equal("NAS 未接続", ex.Message);
        Assert.False(loop.Running); // 状態取得は「停止」を返し、設定リロードなどの要求は受理されない。
        Assert.False(loopRan);
        Assert.Equal(1, stopped);
    }

    [Fact]
    public async Task 起動処理の例外は_同期的に失敗したタスクとして返る()
    {
        // BackgroundService.StartAsync は ExecuteAsync が同期で完了したときだけ失敗を返す(Program.cs が gui.log に残す経路)。
        var loop = new SerialLoop();

        var task = loop.RunAsync(
            start: () => throw new IOException("NAS 未接続"),
            loop: _ => Task.CompletedTask,
            onStopped: () => { },
            onUnexpectedExit: _ => { },
            CancellationToken.None);

        Assert.True(task.IsFaulted);
        Assert.False(loop.Running);
        await Assert.ThrowsAsync<IOException>(() => task);
    }

    [Fact]
    public async Task 稼働中はRunningがtrue_停止要求で止まるとfalseになり_異常終了として残さない()
    {
        var loop = new SerialLoop();
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource();
        var unexpected = 0;
        var stopped = 0;

        var run = loop.RunAsync(
            start: () => { },
            loop: async ct =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
            onStopped: () => stopped++,
            onUnexpectedExit: _ => unexpected++,
            cts.Token);

        await entered.Task;
        Assert.True(loop.Running);

        cts.Cancel();
        await run; // 停止要求は正常終了(例外を漏らさない)。

        Assert.False(loop.Running);
        Assert.Equal(1, stopped);
        Assert.Equal(0, unexpected);
    }

    [Fact]
    public async Task 停止要求でない取り消しでループが抜けたら_異常終了として残す()
    {
        var loop = new SerialLoop();
        Exception? reported = null;
        var reportedCount = 0;

        await loop.RunAsync(
            start: () => { },
            loop: _ => throw new TaskCanceledException("呼んだ先の時間切れ"),
            onStopped: () => { },
            onUnexpectedExit: ex => { reported = ex; reportedCount++; },
            CancellationToken.None);

        Assert.False(loop.Running);
        Assert.Equal(1, reportedCount);
        Assert.IsType<TaskCanceledException>(reported);
    }

    [Fact]
    public async Task 停止要求でないのにループが自然に終わったら_異常終了として残す()
    {
        var loop = new SerialLoop();
        var reportedCount = 0;

        await loop.RunAsync(
            start: () => { },
            loop: _ => Task.CompletedTask,
            onStopped: () => { },
            onUnexpectedExit: ex => { Assert.Null(ex); reportedCount++; },
            CancellationToken.None);

        Assert.Equal(1, reportedCount);
    }

    [Fact]
    public async Task 処理の中の停止要求でない取り消しは失敗として残し_ループは次のトリガで走査を続ける()
    {
        // worker の直列ループと同じ形:トリガを 1 つずつ取り出し、走査を RunStepAsync で走らせる。
        var loop = new SerialLoop();
        using var cts = new CancellationTokenSource();
        var triggers = Channel.CreateUnbounded<string>();
        var scanned = new List<string>();
        var failures = new List<Exception>();
        var secondScanned = new TaskCompletionSource();

        var run = loop.RunAsync(
            start: () => triggers.Writer.TryWrite("起動時"),
            loop: async ct =>
            {
                await foreach (var trigger in triggers.Reader.ReadAllAsync(ct))
                {
                    await SerialLoop.RunStepAsync(
                        _ =>
                        {
                            if (trigger == "起動時")
                                throw new TaskCanceledException("呼んだ先の時間切れ");
                            scanned.Add(trigger);
                            secondScanned.TrySetResult();
                            return Task.CompletedTask;
                        },
                        onFailure: failures.Add,
                        ct);
                }
            },
            onStopped: () => { },
            onUnexpectedExit: ex => Assert.Fail($"ループは抜けない: {ex}"),
            cts.Token);

        triggers.Writer.TryWrite("定期");
        await secondScanned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(loop.Running);
        Assert.Equal(["定期"], scanned);
        Assert.IsType<TaskCanceledException>(Assert.Single(failures));

        cts.Cancel();
        await run;
        Assert.False(loop.Running);
    }

    [Fact]
    public async Task 処理の中の停止要求の取り消しは_中止を知らせてから投げ直す()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stopNotified = 0;
        var failures = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SerialLoop.RunStepAsync(
            ct => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            onFailure: _ => failures++,
            cts.Token,
            onStopRequested: () => stopNotified++));

        Assert.Equal(1, stopNotified);
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task 処理の中の停止要求でない取り消しは_中止ではなく失敗にする()
    {
        // 再適用のジョブ:停止要求でない取り消しは「停止したため中止」ではなく失敗の結果で完了させる。
        var stopNotified = 0;
        Exception? failure = null;

        await SerialLoop.RunStepAsync(
            _ => throw new OperationCanceledException("呼んだ先の取り消し"),
            onFailure: ex => failure = ex,
            CancellationToken.None,
            onStopRequested: () => stopNotified++);

        Assert.Equal(0, stopNotified);
        Assert.IsType<OperationCanceledException>(failure);
    }

    [Fact]
    public async Task 処理のほかの例外は失敗として握る()
    {
        Exception? failure = null;

        await SerialLoop.RunStepAsync(
            _ => throw new InvalidOperationException("想定外"),
            onFailure: ex => failure = ex,
            CancellationToken.None);

        Assert.IsType<InvalidOperationException>(failure);
    }
}
