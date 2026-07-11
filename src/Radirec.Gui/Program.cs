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

        // ログオン自動起動を自己登録(冪等)。
        var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        StartupRegistrar.Register(exePath);

        using var subscriber = new EventLogSummarySubscriber();
        var presenter = new ToastPresenter();

        subscriber.SummaryReceived += payload => presenter.Show(payload);
        subscriber.Warning += message => Console.Error.WriteLine($"[warn] {message}");

        using var trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application, // TODO(実機): 専用アイコンに差し替え
            Text = "Radirec 取込通知",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        subscriber.Start();
        Application.Run();

        trayIcon.Visible = false;
    }

    private static ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("終了", image: null, (_, _) => Application.Exit());
        return menu;
    }
}
