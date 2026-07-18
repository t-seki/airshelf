using System.Runtime.Versioning;
using System.Windows.Forms;
// UI スレッドのメッセージループで発火する WinForms タイマーを使う(コールバックからトレイを直接触れる)。
using Timer = System.Windows.Forms.Timer;

namespace Radirec.Gui;

/// <summary>
/// 更新チェックの自動スケジュール(S6 / #68)。**起動 10 秒後 + 以降 24h ごと**に <see cref="UpdateService"/> で
/// 照会し、初めて更新を見つけたときだけバルーンで知らせる(適用はトレイメニューから)。UI スレッドの
/// WinForms タイマーを使うので、コールバック内でトレイ操作を直接呼べる。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UpdateCoordinator : IDisposable
{
    private const int InitialDelayMs = 10_000;
    private const int PeriodicMs = 24 * 60 * 60 * 1000; // 24h(int に収まる)

    private readonly UpdateService _update;
    private readonly NotifyIcon _tray;
    private readonly Timer _timer;
    private bool _notified;

    public UpdateCoordinator(UpdateService update, NotifyIcon tray)
    {
        _update = update;
        _tray = tray;
        _timer = new Timer { Interval = InitialDelayMs };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        _timer.Interval = PeriodicMs; // 初回以降は 24h 周期
        await CheckAndNotifyAsync(manual: false);
    }

    /// <summary>
    /// 更新を照会する。更新があれば(自動時は初回のみ)バルーン通知。<paramref name="manual"/>=true
    /// (メニューの「今すぐ更新を確認」)は結果を必ずバルーンで返す(最新/更新あり/失敗)。
    /// </summary>
    public async Task CheckAndNotifyAsync(bool manual)
    {
        try
        {
            var found = await _update.CheckAsync();
            if (found && (manual || !_notified))
            {
                _notified = true;
                _tray.ShowBalloonTip(
                    5000, "Radirec",
                    $"更新があります(v{_update.PendingVersion})。トレイメニューから適用できます。",
                    ToolTipIcon.Info);
            }
            else if (manual && !found)
            {
                _tray.ShowBalloonTip(3000, "Radirec", "最新版です。", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"更新チェックに失敗しました: {ex.Message}");
            if (manual)
                _tray.ShowBalloonTip(4000, "Radirec", "更新チェックに失敗しました(gui.log 参照)。", ToolTipIcon.Warning);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
