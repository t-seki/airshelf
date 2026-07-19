using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Radirec.Core.Ingestion;
using Radirec.Hosting;
using Radirec.Hosting.Control;
using Velopack;

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
        // Velopack のフック処理(install/update/uninstall 時に Setup.exe が特殊引数で本 exe を起動する)。
        // 最初に呼ぶ必要がある — フックならここで処理して終了する(通常起動なら素通り。ADR-0013 / S5)。
        // アンインストール時は自己登録したログオン自動起動(HKCU Run)を消す。Velopack は app 自身が
        // 登録した Run 値までは掃除しないため、ここで落として stale なエントリを残さない。
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                // 自動起動(HKCU Run)の掃除は best-effort。group policy 等でレジストリ書込が拒否されても
                // アンインストール自体は失敗させない(掃除漏れはユーザーが手動で消せる)。
                try { StartupRegistrar.Unregister(); }
                catch { /* uninstall を壊さない */ }
            })
            .Run();

        // 単一インスタンス化。トレイがワーカー(台帳オープン・I: 監視)を内包するため、二重起動すると
        // 台帳ロックで片方が縮退したり、最悪二重取り込み + 重複トーストになる。既に起動済みなら黙って終了。
        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\Radirec.Gui.SingleInstance", out var isNew);
        if (!isNew)
            return;

        ApplicationConfiguration.Initialize();

        // 起動シーケンス(自己登録・ホスト構築・トレイ生成)は WinExe でコンソールが無く、失敗すると
        // 無痕跡でトレイが現れないだけになる。ログに残して初回起動失敗を診断可能にする。
        IHost? host = null;
        IServiceControl? control = null;
        string? degradedReason = null; // control==null の理由(縮退メニュー表示用)。
        string? ledgerPath = null;     // 取込履歴(S4)。設定が読めていれば worker 起動可否に依らず参照可。
        UpdateService? updateService = null;       // 自己更新(S6)。設定が読めたときだけ。
        UpdateCoordinator? updateCoordinator = null;
        NotifyIcon trayIcon;
        try
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            StartupRegistrar.Register(exePath); // ログオン自動起動を自己登録(冪等)

            // 設定を読み、取り込みワーカーをこのプロセスでホストする。設定が読めない/ワーカー起動に失敗
            // したときは取り込みを起動せず、トレイ(版表示・終了・再起動)だけを出す縮退にする。
            // 初回起動時は %LocalAppData%\radirec\ に設定を sample から生成する(既存は温存。ADR-0013 / S5)。
            ConfigSeeder.SeedIfAbsent(ConfigPaths.DataRoot);

            var configPath = ConfigPaths.Resolve(null);
            if (ConfigLoading.TryLoad(configPath, out var config, out var error))
            {
                // 設定が読めた時点で台帳パスは判る。worker の起動可否に依らず取込履歴は参照できる。
                ledgerPath = config!.LedgerPath;

                // 自己更新(S6)。dev/portable(未 Velopack)では UpdateService.IsInstalled が false になり
                // 更新 UI は出ない。生成自体が失敗してもトレイは壊さない。
                try
                {
                    updateService = new UpdateService(config.UpdateToken, config.UpdatePrerelease);
                }
                catch (Exception ex)
                {
                    TrayLog.Warn($"自己更新の初期化に失敗しました(更新無効で継続): {ex.Message}");
                }
                try
                {
                    host = BuildHost(config!, configPath);
                    // ワーカーを解決(ctor = 台帳オープン。ローカルディスクで速い)。ここで throw すれば縮退。
                    control = host.Services.GetRequiredService<RadioMonitorWorker>();
                    // ★ host の起動は UI スレッドで走らせない。BackgroundService の同期プレフィックス
                    // (CleanupOrphans が I: = NAS を列挙、StartWatcher)は最初の await までホスト起動を
                    // 呼んだスレッドで動くため、ログオン時に NAS 未接続だと数十秒ハングしトレイ生成を
                    // ブロックしうる。別スレッドで起動してトレイを即出す(ハングは try/catch で捕まらない)。
                    _ = Task.Run(() => host!.StartAsync())
                        .ContinueWith(
                            t => TrayLog.Warn($"取り込みワーカーのホスト起動が失敗しました(状態は「停止」に反映): {t.Exception}"),
                            TaskContinuationOptions.OnlyOnFaulted);
                    TrayLog.Info("取り込みワーカーを起動しました");
                }
                catch (Exception ex)
                {
                    TrayLog.Warn($"取り込みワーカーの起動に失敗しました。トレイのみ表示します: {ex}");
                    StopHost(host);
                    host = null;
                    control = null;
                    degradedReason = "取り込みの起動に失敗しました(gui.log 参照)";
                }
            }
            else
            {
                TrayLog.Warn($"設定の読み込みに失敗しました({error})。取り込みは起動せずトレイのみ表示します");
                degradedReason = "設定を読み込めませんでした";
            }

            trayIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "Radirec",
                Visible = true,
            };
            // 自己更新の自動チェック(起動時 + 24h。S6)。**Velopack インストール時のみ**実働する
            // (dev/portable で no-op タイマーを作らない)。生成失敗はトレイを壊さない。
            if (updateService?.IsInstalled == true)
            {
                try
                {
                    updateCoordinator = new UpdateCoordinator(updateService, trayIcon);
                }
                catch (Exception ex)
                {
                    TrayLog.Warn($"更新チェックの初期化に失敗しました(更新無効で継続): {ex.Message}");
                    updateCoordinator = null;
                }
            }

            // 更新適用直前にワーカーを graceful 停止するコールバック(在中スキャン完了・SQLite クローズ →
            // 取込中断/ファイルロックを避ける。S6 レビュー)。ワーカー未起動時は null。
            var capturedHost = host;
            Func<Task>? drainWorker = capturedHost is null
                ? null
                : async () =>
                {
                    try { await capturedHost.StopAsync(TimeSpan.FromSeconds(15)); }
                    catch (Exception ex) { TrayLog.Warn($"更新適用前のワーカー停止で例外: {ex.Message}"); }
                };

            // 制御(rescan/status/reload)を in-process でトレイメニューに載せる(ADR-0012)。
            // control が null(縮退)なら理由を表示し、設定修正後に復帰できるよう再起動導線を出す。
            trayIcon.ContextMenuStrip = new ControlTrayMenu(
                trayIcon, control, degradedReason, ledgerPath, updateService, updateCoordinator, drainWorker).Menu;

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

        // 更新チェックタイマーを止め、ワーカーを graceful に停止する。
        updateCoordinator?.Dispose();
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
