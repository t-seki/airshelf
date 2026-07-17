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

            // 不在中サマリ(軸A A2): 前回の購読位置を読み、停止中に溜まった分を起動時に集約提示する。
            var checkpoints = new CheckpointStore();
            subscriber = new EventLogSummarySubscriber(checkpoints.Load());
            var presenter = new ToastPresenter();

            // Show() が投げても subscriber 側の try/catch が握るため(Warning に流れる)、ここは素直に呼ぶ。
            subscriber.SummaryReceived += payload => presenter.Show(payload);
            subscriber.WhileAwaySummaryReceived += payload => presenter.ShowWhileAway(payload);
            subscriber.CheckpointAdvanced += checkpoints.Save;
            subscriber.Warning += TrayLog.Warn;

            trayIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
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

    /// <summary>
    /// 専用トレイアイコン(#52)を埋め込みリソースからロードする。<c>PublishSingleFile</c> のため
    /// loose file ではなく EmbeddedResource(LogicalName は csproj で固定)。<see cref="SystemInformation.SmallIconSize"/>
    /// を指定して ico 内の実寸フレーム(16px 相当)を選ばせる(既定だと 32px を縮小して潰れる)。
    /// アイコン欠損でトレイ全体を殺さないよう、失敗時は従来の既定アイコンへフォールバックする。
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            using var stream = typeof(Program).Assembly
                .GetManifestResourceStream("Radirec.Gui.Assets.radirec.ico");
            if (stream is null)
            {
                TrayLog.Warn("トレイアイコン資産が見つかりません。既定アイコンで継続します");
                return SystemIcons.Application;
            }
            return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            // 実機でしか再現しない失敗のためログが一次情報。起動失敗ハンドラと同じ粒度で {ex} 全体を残す。
            TrayLog.Warn($"トレイアイコンのロードに失敗。既定アイコンで継続します: {ex}");
            return SystemIcons.Application;
        }
    }
}
