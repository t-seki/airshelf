using Radirec.Gui.Core.Updates;

namespace Radirec.Tests;

// 更新の適用の再入を防ぐ(#157)。UpdateService(Windows 専用)の ApplyAndRestartAsync はこれで包む。
public sealed class ApplyGuardTests
{
    [Fact]
    public async Task SecondCallWhileRunning_DoesNothing()
    {
        var guard = new ApplyGuard();
        var hold = new TaskCompletionSource();
        var runs = 0;

        var first = guard.RunAsync(async () =>
        {
            runs++;
            await hold.Task; // ダウンロードを待っている間
        });
        var second = await guard.RunAsync(() =>
        {
            runs++;
            return Task.CompletedTask;
        });

        Assert.True(guard.IsRunning);
        Assert.False(second);
        hold.SetResult();
        Assert.True(await first);
        Assert.Equal(1, runs);
        Assert.False(guard.IsRunning);
    }

    [Fact]
    public async Task AfterFinish_CanRunAgain()
    {
        var guard = new ApplyGuard();

        Assert.True(await guard.RunAsync(() => Task.CompletedTask));
        Assert.True(await guard.RunAsync(() => Task.CompletedTask));
    }

    [Fact]
    public async Task Failure_ReleasesGuardAndRethrows()
    {
        var guard = new ApplyGuard();

        await Assert.ThrowsAsync<IOException>(() => guard.RunAsync(() => Task.FromException(new IOException("disk"))));

        Assert.False(guard.IsRunning); // もう一度押せる
        Assert.True(await guard.RunAsync(() => Task.CompletedTask));
    }
}
