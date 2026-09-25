using Radirec.Gui.Core;
using Radirec.Hosting.Control;

namespace Radirec.Tests;

// 状態の文言(docs/mockups/management-window.md の W-101〜W-108)。トレイと管理画面のホームが共有する(#151)。
public sealed class ServiceStatusTextTests
{
    internal static readonly TimeZoneInfo Jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    internal static ServiceStatusSnapshot Snapshot(
        bool running = true, bool watching = true, DateTimeOffset? completed = null,
        string? trigger = null, int imported = 0, int failed = 0) => new()
    {
        Version = "0.0.0-dev",
        StartedUtc = DateTimeOffset.UnixEpoch,
        RadioRoot = @"I:\radio",
        Watching = watching,
        Running = running,
        LastScanCompletedUtc = completed,
        LastScanTrigger = trigger,
        LastImported = imported,
        LastFailed = failed,
    };

    [Fact]
    public void Watching_W101_with_last_scan_W106()
    {
        var view = ServiceStatusText.From(
            Snapshot(completed: new DateTimeOffset(2026, 9, 25, 1, 42, 0, TimeSpan.Zero), trigger: "検知", imported: 3), Jst);

        Assert.Equal("状態: 稼働中(監視中)", view.Status);
        Assert.Equal("最終スキャン: 9/25 10:42 検知(取込 3)", view.LastScan);
        Assert.True(view.CanControl);
    }

    [Fact]
    public void Polling_W102_with_failures_W106()
    {
        var view = ServiceStatusText.From(
            Snapshot(watching: false, completed: new DateTimeOffset(2026, 9, 25, 1, 40, 0, TimeSpan.Zero), trigger: "定期", failed: 1), Jst);

        Assert.Equal("状態: 稼働中(定期スキャンのみ)", view.Status);
        Assert.Equal("最終スキャン: 9/25 10:40 定期(取込 0・失敗 1)", view.LastScan);
    }

    [Fact]
    public void Not_scanned_yet_W107()
    {
        var view = ServiceStatusText.From(Snapshot(), Jst);

        Assert.Equal("最終スキャン: 未走査", view.LastScan);
        Assert.True(view.CanControl);
    }

    [Fact]
    public void Crashed_worker_W103_W108_and_no_control()
    {
        var view = ServiceStatusText.From(
            Snapshot(running: false, completed: DateTimeOffset.UnixEpoch, trigger: "検知"), Jst);

        Assert.Equal("状態: 停止(取り込みが異常終了。gui.log 参照 → 再起動)", view.Status);
        Assert.Equal("最終スキャン: —", view.LastScan);
        Assert.False(view.CanControl);
    }

    [Fact]
    public void Start_failed_W104()
    {
        var view = ServiceStatusText.Stopped(ServiceStatusText.DegradedStartFailed);

        Assert.Equal("状態: 取り込み停止中(取り込みの起動に失敗しました(gui.log 参照))", view.Status);
        Assert.Equal("最終スキャン: —", view.LastScan);
        Assert.False(view.CanControl);
    }

    [Fact]
    public void Config_unreadable_W105()
    {
        var view = ServiceStatusText.Stopped(ServiceStatusText.DegradedConfigUnreadable);

        Assert.Equal("状態: 取り込み停止中(設定を読み込めませんでした)", view.Status);
        Assert.False(view.CanControl);
    }
}
