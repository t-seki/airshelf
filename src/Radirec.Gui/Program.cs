using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Radirec.Core.Ingestion;
using Radirec.Gui.Core;
using Radirec.Gui.Core.Management;
using Radirec.Gui.Core.Updates;
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

        // UI の例外がワーカーを巻き込まないようにする(roadmap §地図から読める構造 の 1.・#151 の設計の決定 5)。
        // UI スレッドの未捕捉例外は WinForms(トレイ)・WPF(管理画面)の両方で捕まえ、gui.log に書いて握り、
        // 管理画面だけを閉じる(トレイとワーカーは生かす)。SetUnhandledExceptionMode はコントロールを 1 つでも
        // 作った後に呼ぶと例外になるので、トレイを作る前に置く。
        ManagementWindowHost? managementHost = null; // トレイを作った後に入る(それまでの例外はログだけ)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnUnhandledUiException(managementHost, e.Exception, "Application.ThreadException");
        System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            OnUnhandledUiException(managementHost, e.Exception, "Dispatcher.UnhandledException");
            e.Handled = true;
        };
        // 別スレッドの未捕捉例外はプロセスの終了を止められないので、ログに残すだけ。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            TrayLog.Error($"捕まえていない例外(IsTerminating={e.IsTerminating}): {e.ExceptionObject}");

        // 起動シーケンス(自己登録・ホスト構築・トレイ生成)は WinExe でコンソールが無く、失敗すると
        // 無痕跡でトレイが現れないだけになる。ログに残して初回起動失敗を診断可能にする。
        IHost? host = null;
        IServiceControl? control = null;
        string? degradedReason = null; // control==null の理由(縮退メニュー表示用)。
        string? ledgerPath = null;     // 管理画面の取込履歴(S4 / #151)。設定が読めていれば worker 起動可否に依らず参照可。
        string? configPath = null;          // 設定ページで開く(#143 / #152)。読めなかったときも開ける(直すため)。
        RadirecConfig? startupConfig = null; // 同上。ワーカーが無いときの programs/stations の場所の出どころ。
        string? configError = null;          // radirec.json を読めなかった理由(設定ページの縮退パネル W-402。#152)。
        string? startError = null;           // 設定は読めたがワーカーを起動できなかった理由(設定ページの先頭のパネル W-407。#168)。
        var toastSetting = IngestToastSetting.Fixed(true); // 新規取込のトースト(#152)。下で設定ファイルから読む。
        UpdateService? updateService = null;       // 自己更新(S6)。設定が読めたときだけ。
        UpdateChecker? updateChecker = null;       // 更新の確認。トレイの自動の確認と管理画面で共有する(#157)。
        UpdateCoordinator? updateCoordinator = null;
        ControlPipeServer? controlServer = null; // CLI からの状態取得・設定リロードの受け口(ADR-0020 / #179)。縮退時も起動する。
        NotifyIcon trayIcon;
        try
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            StartupRegistrar.Register(exePath); // ログオン自動起動を自己登録(冪等)

            // 設定を読み、取り込みワーカーをこのプロセスでホストする。設定が読めない/ワーカー起動に失敗
            // したときは取り込みを起動せず、トレイ(版表示・終了・再起動)だけを出す縮退にする。
            // 初回起動時はデータルート(%LOCALAPPDATA%\RadirecData\)に設定を sample から生成する
            // (既存は温存。ADR-0013 / S5)。Velopack のインストール先 %LOCALAPPDATA%\Radirec ではない。
            ConfigSeeder.SeedIfAbsent(ConfigPaths.DataRoot);

            configPath = ConfigPaths.Resolve(null);
            var loaded = ConfigLoading.TryLoad(configPath, out var config, out var error);
            // 新規取込のトーストを出すか(#152・ADR-0019)。管理画面用の設定ファイル(radirec.json と同じフォルダ)に持ち、
            // まだ値が無ければ radirec.json の旧設定 NotificationsEnabled を初期値にする。読めない・書けないときも起動は続ける。
            var guiSettingsPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(configPath)) ?? ConfigPaths.DataRoot,
                GuiSettingsStore.FileName);
            toastSetting = IngestToastSetting.Load(
                new GuiSettingsStore(guiSettingsPath, TrayLog.Warn), loaded ? config!.NotificationsEnabled : null, TrayLog.Warn);
            if (loaded)
            {
                startupConfig = config;
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
                    host = BuildHost(config!, configPath, toastSetting);
                    // ワーカーを解決(ctor = 台帳オープン。ローカルディスクで速い)。ここで throw すれば縮退。
                    control = host.Services.GetRequiredService<RadioMonitorWorker>();
                    // host の起動は UI スレッドで待たない(トレイを即出す)。.NET 10 から BackgroundService は ExecuteAsync の
                    // 全体を別のタスクで走らせるので、起動処理(CleanupOrphans が NAS を列挙、StartWatcher)がハングしても
                    // ホストの起動は待たされない。起動処理の失敗もここには伝わらず、ワーカーが gui.log と状態に残す(#217)。
                    // ここで捕まるのは、ホスト自身の起動の失敗だけ。
                    // 起動処理の結果(開始できたか・失敗したか)は、この後にワーカーが gui.log に書く。この行はホストの起動を
                    // 走らせる前に書く(後に書くと、別スレッドのワーカーの「起動処理を始めます」が先に出うる)。
                    TrayLog.Info("取り込みワーカーの起動を始めました");
                    _ = Task.Run(() => host!.StartAsync())
                        .ContinueWith(
                            t => TrayLog.Warn($"取り込みワーカーのホスト起動が失敗しました: {t.Exception}"),
                            TaskContinuationOptions.OnlyOnFaulted);
                }
                catch (Exception ex)
                {
                    TrayLog.Warn($"取り込みワーカーの起動に失敗しました。トレイのみ表示します: {ex}");
                    StopHost(host);
                    host = null;
                    control = null;
                    degradedReason = ServiceStatusText.DegradedStartFailed; // W-104
                    // 例外の全文は上で gui.log に書いた。画面にはメッセージだけを出す(programs.json の JSON の誤り等が読める)。
                    startError = ex.Message;
                }
            }
            else
            {
                // error は前置き(「設定の読み込みに失敗しました: 」など)を含む(ConfigLoading.TryLoad)。重ねない(#218)。
                TrayLog.Warn($"{error}。取り込みは起動せずトレイのみ表示します");
                configError = error;
                degradedReason = ServiceStatusText.DegradedConfigUnreadable; // W-105
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
                    updateChecker = new UpdateChecker(updateService, TrayLog.Warn);
                    updateCoordinator = new UpdateCoordinator(updateChecker, trayIcon);
                }
                catch (Exception ex)
                {
                    TrayLog.Warn($"更新チェックの初期化に失敗しました(更新無効で継続): {ex.Message}");
                    updateChecker = null;
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
            if (updateService is not null)
                updateService.DrainBeforeRestart = drainWorker; // 管理画面から適用するとき(#157)

            // 管理画面(#151)。トレイの「Radirec を開く」とアイコンのダブルクリックで開く。縮退時も開ける。
            // バージョン情報ページ(#157)は、更新の確認が作れたとき(Velopack で入れた版)だけ更新の項目を出す。
            // 番組ページ(#154)は、設定ページと同じ radirec.json の場所と起動時に読めた設定から programs.json の場所を求める。
            var windowHost = new ManagementWindowHost(
                trayIcon, control, degradedReason, ledgerPath,
                new SettingsPageContext(configPath, startupConfig, configError, toastSetting, startError),
                DecideUpdateSupport(updateService, configLoaded: startupConfig is not null),
                updateChecker is null ? null : updateService, updateChecker);
            managementHost = windowHost;
            trayIcon.DoubleClick += (_, _) => windowHost.Show();

            // 制御(rescan/status)を in-process でトレイメニューに載せる(ADR-0012。reload と再適用は管理画面。#152 / #154)。
            // control が null(縮退)なら理由を表示し、設定修正後に復帰できるよう再起動導線を出す。
            trayIcon.ContextMenuStrip = new ControlTrayMenu(
                trayIcon, windowHost.Show, control, degradedReason, updateService, drainWorker).Menu;

            // CLI(radirec status / reload)の受け口(ADR-0020 / #179)。取り込みが止まっている(縮退)ときも起動し、
            // 理由を答える。受け口の失敗ではトレイと取り込みを止めない。
            try
            {
                controlServer = new ControlPipeServer(
                    ControlProtocol.DefaultPipeName(), control, degradedReason, configPath,
                    AppVersion.Of(typeof(Program).Assembly), TrayLog.Warn);
                controlServer.Start();
            }
            catch (Exception ex)
            {
                TrayLog.Warn($"制御チャネルの受け口を起動できませんでした(CLI の status / reload は使えません): {ex}");
                controlServer = null;
            }

            TrayLog.Info("Radirec トレイを起動");
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"トレイ起動に失敗: {ex}");
            StopControlServer(controlServer);
            StopHost(host);
            return;
        }

        using (trayIcon)
        {
            Application.Run();
            trayIcon.Visible = false;
        }

        // 更新チェックタイマーと制御チャネルの受け口を止め、ワーカーを graceful に停止する。
        updateCoordinator?.Dispose();
        StopControlServer(controlServer);
        StopHost(host);
    }

    // 制御チャネルの受け口を止める(処理中の要求が終わるのを最大 5 秒待つ)。失敗しても終了は続ける。
    private static void StopControlServer(ControlPipeServer? server)
    {
        if (server is null)
            return;
        try
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"制御チャネルの受け口の停止で例外(続行します): {ex.Message}");
        }
    }

    // バージョン情報ページで更新の項目を出せるか(#157)。インストール済みかは、更新の仕組みが作れていればそれに、
    // 作れていない(設定を読めない等)ならトークン無しで調べる(UpdateService.ProbeInstalled)。
    // - Velopack で入れていない版 → W-510(設定を読めるかに依らない。開発版・portable ではそもそも更新できない)
    // - 入れた版で設定を読めない → W-511(調べられなかったときも、設定を読めないのは確かなのでこちら)
    private static UpdateSupport DecideUpdateSupport(UpdateService? updateService, bool configLoaded)
    {
        var installed = updateService is not null ? updateService.IsInstalled : UpdateService.ProbeInstalled();
        if (installed == false)
            return UpdateSupport.NotInstalled;
        return configLoaded ? UpdateSupport.Supported : UpdateSupport.ConfigUnreadable;
    }

    // UI スレッドの未捕捉例外の受け口。管理画面があればそれだけを閉じる(ManagementWindowHost)。トレイを作る前はログだけ。
    private static void OnUnhandledUiException(ManagementWindowHost? host, Exception ex, string source)
    {
        try
        {
            if (host is null)
                TrayLog.Error($"UI スレッドで捕まえていない例外({source}): {ex}");
            else
                host.OnUnhandledUiException(ex, source);
        }
        catch (Exception inner)
        {
            // 最後の網からさらに例外を漏らすと、メッセージループごとトレイが落ちる。
            TrayLog.Error($"UI スレッドの例外の処理中に例外: {inner}(元の例外: {ex})");
        }
    }

    /// <summary>取り込みワーカー + in-process トースト publisher + ファイルログを載せた汎用ホストを組む。</summary>
    private static IHost BuildHost(RadirecConfig config, string configPath, IngestToastSetting toastSetting)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(new ServiceRuntimeOptions(configPath));

        // 取込通知は EventLog を経ず in-process のトーストで発行する(ADR-0012)。成功要約・失敗の
        // 両インタフェースを同一インスタンスで満たし、ワーカーへ両方注入する。新規取込(成功要約)のトーストだけは
        // 設定ページで止められる(#152)。失敗のトーストはこの設定を通さないので常に出る。
        var toastPublisher = new ToastNotificationPublisher(new ToastPresenter());
        builder.Services.AddSingleton<IIngestSummaryPublisher>(
            new GatedIngestSummaryPublisher(toastPublisher, () => toastSetting.Enabled));
        builder.Services.AddSingleton<IIngestFailurePublisher>(toastPublisher);
        // 保留(差し替え放送の疑い。ADR-0021)のトーストも、失敗と同じくこの設定を通さない。
        builder.Services.AddSingleton<IIngestHeldPublisher>(toastPublisher);

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
