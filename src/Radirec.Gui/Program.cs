using System.Drawing;
using System.Windows.Forms;
using Radirec.Gui.Core;

namespace Radirec.Gui;

/// <summary>
/// トレイ常駐の受け手アプリのブートストラップ(ADR-0009 の tracer slice)。
/// 起動時に自動起動を自己登録し、EventID 1001 を購読 → 文面組み立て → ネイティブトースト表示。
/// UI は最小(トレイアイコン + 終了メニュー)。編集 UI は #5 本体で後載せ。
/// ビルド/実行は Windows のみ。E2E は実機検証(サービス取込 → 1001 → トースト)。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // 起動シーケンス(購読開始・自己登録・トレイ生成)は WinExe でコンソールが無く、失敗すると
        // 無痕跡でトレイが現れないだけになる。ログに残して初回起動失敗を診断可能にする。
        EventLogSummarySubscriber subscriber;
        NotifyIcon trayIcon;
        try
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            StartupRegistrar.Register(exePath); // ログオン自動起動を自己登録(冪等)

            subscriber = new EventLogSummarySubscriber();
            var presenter = new ToastPresenter();

            // Show() が投げても subscriber 側の try/catch が握るため(Warning に流れる)、ここは素直に呼ぶ。
            subscriber.SummaryReceived += payload => presenter.Show(payload);
            subscriber.Warning += TrayLog.Warn;

            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application, // TODO(実機): 専用アイコンに差し替え
                Text = "Radirec 取込通知",
                Visible = true,
            };
            // 制御チャネル窓口(rescan/status/reload。ADR-0010 / 軸B)をトレイメニューに載せる。
            trayIcon.ContextMenuStrip = new ControlTrayMenu(trayIcon).Menu;

            subscriber.Start();
            TrayLog.Info("Radirec 取込通知トレイを起動");
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"トレイ起動に失敗: {ex}");
            return;
        }

        using (subscriber)
        using (trayIcon)
        {
            Application.Run();
            trayIcon.Visible = false;
        }
    }
}
