using System.Runtime.Versioning;
using System.Windows.Forms;
using Airshelf.Gui.Core.Updates;
// UI スレッドのメッセージループで発火する WinForms タイマーを使う(コールバックからトレイを直接触れる)。
using Timer = System.Windows.Forms.Timer;

namespace Airshelf.Gui;

/// <summary>
/// 更新チェックの自動スケジュール(S6 / #68)。**起動 10 秒後 + 以降 24h ごと**に <see cref="UpdateChecker"/> で
/// 照会し、初めて更新を見つけたときだけバルーンで知らせる(W-609。適用は管理画面のバージョン情報ページかトレイから)。
/// 手動の確認は管理画面のバージョン情報ページが同じ <see cref="UpdateChecker"/> で行う(#157。重なりの判定を共有する)。
/// UI スレッドの WinForms タイマーを使うので、コールバック内でトレイ操作を直接呼べる。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UpdateCoordinator : IDisposable
{
    private const int InitialDelayMs = 10_000;
    private const int PeriodicMs = 24 * 60 * 60 * 1000; // 24h(int に収まる)

    private readonly UpdateChecker _checker;
    private readonly NotifyIcon _tray;
    private readonly Timer _timer;

    public UpdateCoordinator(UpdateChecker checker, NotifyIcon tray)
    {
        _checker = checker;
        _tray = tray;
        _timer = new Timer { Interval = InitialDelayMs };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        _timer.Interval = PeriodicMs; // 初回以降は 24h 周期
        try
        {
            // 手動の確認と重なったら(Busy)黙って諦める(次回 tick で拾う)。失敗は UpdateChecker が gui.log に書く。
            var result = await _checker.CheckAsync(manual: false);
            if (result.NotifyBalloon)
                _tray.ShowBalloonTip(5000, "Airshelf", UpdateTexts.FoundBalloon(result.Version), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            // async void の例外はメッセージループへ抜けるので、ここで止める(UpdateChecker は照会の例外を握るので、ここはバルーンの失敗など)。
            TrayLog.Warn($"更新の自動チェックで例外: {ex}");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
