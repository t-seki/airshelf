using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Radirec.Core.Ingestion;
using Radirec.Hosting;
using Radirec.Hosting.Control;

namespace Radirec.Gui;

/// <summary>
/// トレイ常駐アプリのブートストラップ(ADR-0012)。取り込みワーカーをこのユーザーセッションプロセスで
/// ホストし(<see cref="RadioMonitorWorker"/>)、取込通知は in-process で直接トースト表示する。制御
/// (rescan/reload/status)はトレイメニューから <see cref="IServiceControl"/> を直接呼ぶ。
/// 起動時に自動起動を自己登録する。ビルド/実行は Windows のみ。E2E は実機検証。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // 起動シーケンス(自己登録・ホスト構築・トレイ生成)は WinExe でコンソールが無く、失敗すると
        // 無痕跡でトレイが現れないだけになる。ログに残して初回起動失敗を診断可能にする。
        IHost? host = null;
        IServiceControl? control = null;
        NotifyIcon trayIcon;
        try
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            StartupRegistrar.Register(exePath); // ログオン自動起動を自己登録(冪等)

            // 設定を読み、取り込みワーカーをこのプロセスでホストする。設定が読めないときは取り込みを
            // 起動せず、トレイ(版表示・終了)だけを出す縮退にする(無痕跡で消えるより診断可能)。
            var configPath = ConfigPaths.Resolve(null);
            if (ConfigLoading.TryLoad(configPath, out var config, out var error))
            {
                try
                {
                    host = BuildHost(config!, configPath);
                    // 起動は独自 try で包む。ワーカー構築(台帳オープン)や起動時スキャンが失敗しても
                    // (ログオン時点で NAS 未接続・SQLite ロック・stations/programs 解析失敗等)、ここで
                    // return せず control=null の縮退トレイへフォールバックする。無痕跡でトレイが消えるより
                    // 「アプリは生きているが取り込み停止中」を可視化する(旧受け手トレイと同じ耐性)。
                    host.Start();
                    control = host.Services.GetRequiredService<RadioMonitorWorker>();
                    TrayLog.Info("取り込みワーカーを起動しました");
                }
                catch (Exception ex)
                {
                    TrayLog.Warn($"取り込みワーカーの起動に失敗しました。トレイのみ表示します: {ex}");
                    StopHost(host);
                    host = null;
                    control = null;
                }
            }
            else
            {
                TrayLog.Warn($"設定の読み込みに失敗しました({error})。取り込みは起動せずトレイのみ表示します");
            }

            trayIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "Radirec",
                Visible = true,
            };
            // 制御(rescan/status/reload)を in-process でトレイメニューに載せる(ADR-0012)。
            // control が null(設定未読込)ならメニューは縮退表示する。
            trayIcon.ContextMenuStrip = new ControlTrayMenu(trayIcon, control).Menu;

            TrayLog.Info("Radirec トレイを起動");
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"トレイ起動に失敗: {ex}");
            StopHost(host);
            return;
        }

        using (trayIcon)
        {
            Application.Run();
            trayIcon.Visible = false;
        }

        // メッセージループ終了(終了メニュー)後、ワーカーを graceful に停止する。
        StopHost(host);
    }

    /// <summary>取り込みワーカー + in-process トースト publisher + ファイルログを載せた汎用ホストを組む。</summary>
    private static IHost BuildHost(RadirecConfig config, string configPath)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(new ServiceRuntimeOptions(configPath));

        // 取込通知は EventLog を経ず in-process のトーストで発行する(ADR-0012)。成功要約・失敗の
        // 両インタフェースを同一インスタンスで満たし、ワーカーへ両方注入する。
        var toastPublisher = new ToastNotificationPublisher(new ToastPresenter());
        builder.Services.AddSingleton<IIngestSummaryPublisher>(toastPublisher);
        builder.Services.AddSingleton<IIngestFailurePublisher>(toastPublisher);

        // worker は取込ワーカー兼 in-process 制御の IServiceControl。同一インスタンスを
        // HostedService と IServiceControl(トレイメニューが参照)で共有する。
        builder.Services.AddSingleton<RadioMonitorWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RadioMonitorWorker>());
        builder.Services.AddSingleton<IServiceControl>(sp => sp.GetRequiredService<RadioMonitorWorker>());

        // コンソールも EventLog も無いので、動作ログはユーザーの gui.log へ出す。
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new TrayLogLoggerProvider());

        return builder.Build();
    }

    private static void StopHost(IHost? host)
    {
        if (host is null)
            return;
        try
        {
            // ワーカーの在中スキャン完了を待って停止する(StopAsync は BackgroundService の終了を待つ)。
            host.StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"ホスト停止で例外(続行して破棄します): {ex}");
        }
        finally
        {
            host.Dispose();
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
