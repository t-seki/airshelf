using Radirec.Gui.Core;

namespace Radirec.Tests;

// トーストのクリックで管理画面のホームを開く(#237)。クリックの受け取りと画面を開く処理は Radirec.Gui(実機で確かめる)。
public sealed class ToastClickTests
{
    [Theory]
    [InlineData(IngestToastKind.Held)]
    [InlineData(IngestToastKind.Failure)]
    public void Held_and_failure_toasts_open_home(IngestToastKind kind) =>
        Assert.True(ToastClick.OpensHome(ToastClick.ActionFor(kind)));

    [Fact]
    public void Summary_toast_carries_no_action() =>
        Assert.Null(ToastClick.ActionFor(IngestToastKind.Summary));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OpenHome")]
    [InlineData("other")]
    public void Other_actions_do_not_open_home(string? action) =>
        Assert.False(ToastClick.OpensHome(action));

    [Fact]
    public void Request_before_attach_opens_once_on_attach()
    {
        var requests = new PendingOpenRequests();
        requests.Request();
        requests.Request(); // 起動の途中に 2 度クリックされても、開くのは 1 度

        var opens = 0;
        requests.Attach(() => opens++);
        Assert.Equal(1, opens);

        requests.Request(); // 渡した後のクリックは、そのたびに開く(開いていれば前面に出す)
        Assert.Equal(2, opens);
    }

    [Fact]
    public void Attach_without_request_does_not_open()
    {
        var requests = new PendingOpenRequests();
        var opens = 0;
        requests.Attach(() => opens++);
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task Request_from_other_threads_while_attaching_opens_at_least_once()
    {
        var requests = new PendingOpenRequests();
        var opens = 0;
        var clicks = Enumerable.Range(0, 8).Select(_ => Task.Run(requests.Request)).ToArray();
        requests.Attach(() => Interlocked.Increment(ref opens));
        await Task.WhenAll(clicks);

        Assert.InRange(opens, 1, 8); // 取りこぼさない(渡す前の分はまとめて 1 度、後の分はそのたび)
    }
}
