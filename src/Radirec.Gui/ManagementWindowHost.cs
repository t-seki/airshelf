using System.Runtime.Versioning;
using System.Windows.Forms;
using Radirec.Gui.Core;
using Radirec.Gui.Core.Management;
using Radirec.Gui.Core.Updates;
using Radirec.Hosting;
using Radirec.Hosting.Control;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using WindowState = System.Windows.WindowState;

namespace Radirec.Gui;

/// <summary>
/// 管理画面(#151)を開く・前面に出す・エラーで閉じる。同時に開けるのは 1 つだけで、2 回目は開いているものを
/// 前面に出す。閉じても常駐(トレイとワーカー)は続く。すべて UI スレッドから呼ぶ。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ManagementWindowHost
{
    /// <summary>W-004。UI スレッドの未捕捉例外で管理画面を閉じたときのバルーン(#151 の設計の決定 5)。</summary>
    public const string ClosedAfterErrorBalloon = "管理画面でエラーが起きたため閉じました(gui.log 参照)";

    private readonly NotifyIcon _tray;
    private readonly IServiceControl? _control;
    private readonly string? _degradedReason;
    private readonly string? _ledgerPath;
    private readonly SettingsPageContext _settings;
    private readonly UpdateSupport _updateSupport;
    private readonly IAppUpdater? _updater;
    private readonly UpdateChecker? _updateChecker;
    private ManagementWindow? _window;
    private ManagementViewModel? _viewModel; // 開いている管理画面の ViewModel(_window と対)
    private bool _closingAfterError;

    /// <param name="control">取り込みワーカーの制御面。縮退(ワーカーが無い)なら null。</param>
    /// <param name="degradedReason">control が null の理由(W-104 / W-105)。</param>
    /// <param name="ledgerPath">
    /// 起動時に読めた設定の台帳の絶対パス。設定を読めず場所が決まらないなら null(W-150)。ワーカーがあるときは、読むたびに
    /// 今の状態の台帳の場所を使う(設定リロードに追従する。#196)。
    /// </param>
    /// <param name="settings">
    /// 設定ページ(#152)に渡すもの。radirec.json の場所と起動時に読めた設定は、番組ページ(#154)の programs.json の場所の
    /// 計算にも使う。
    /// </param>
    /// <param name="updateSupport">バージョン情報ページで更新の項目を出せるか。出せないときの理由(W-510 / W-511。#157)。</param>
    /// <param name="updater">自己更新(#157)。作れなかった・Velopack で入れていない版なら null。</param>
    /// <param name="updateChecker">更新の確認(トレイの自動の確認と共有する。#157)。</param>
    public ManagementWindowHost(
        NotifyIcon tray, IServiceControl? control, string? degradedReason, string? ledgerPath, SettingsPageContext settings,
        UpdateSupport updateSupport, IAppUpdater? updater = null, UpdateChecker? updateChecker = null)
    {
        _tray = tray;
        _control = control;
        _degradedReason = degradedReason;
        _ledgerPath = ledgerPath;
        _settings = settings;
        _updateSupport = updateSupport;
        _updater = updater;
        _updateChecker = updateChecker;
    }

    /// <summary>管理画面を開く。開いていれば前面に出す(最小化していれば元の大きさに戻してから)。</summary>
    public void Show()
    {
        try
        {
            if (_window is { } open)
            {
                // Activate(= SetForegroundWindow)は最小化を解かないので、押しても反応が無いように見えるのを防ぐ。
                if (open.WindowState == WindowState.Minimized)
                    open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }

            // 台帳の場所は読むたびに決め直す(設定リロードで LedgerPath が変わったら、次の読み込みから新しい台帳。#196)。
            var ledger = LedgerLocator.Create(_control, _ledgerPath);
            Func<IReadOnlyList<IngestHistoryRow>>? loadHistory = ledger is null ? null : () => IngestHistory.Load(ledger());
            var about = new AboutPageViewModel(
                AppVersion.Of(typeof(ManagementWindowHost).Assembly), _updateSupport, _updater, _updateChecker,
                ConfirmApplyUpdate, TrayLog.Warn);
            var settings = new SettingsPageViewModel(
                _control, _settings.ConfigPath, _settings.StartupConfig, _settings.ConfigError,
                _settings.ToastSetting, new SettingsPageActions(), TrayLog.Warn, _settings.StartError);
            var viewModel = new ManagementViewModel(
                _control, _degradedReason, loadHistory, new HistoryPathActions(), TrayLog.Warn,
                locateProgramsFile: ProgramsFileLocator(), logInfo: TrayLog.Info, about: about, settings: settings,
                computeAttention: AttentionComputer());

            var window = new ManagementWindow(viewModel);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, window))
                {
                    _window = null;
                    _viewModel = null;
                }
            };
            _window = window;
            _viewModel = viewModel;
            // WinForms のループ(Application.Run)の上でモードレスに開く WPF ウィンドウには WM_CHAR が届かず、
            // 半角英数字を入力できない(IME の確定入力だけが入る)。キーボードのメッセージを WPF へ回す(#169)。
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(window);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            // WPF の初期化や Show の失敗をトレイのクリックのハンドラから漏らさない(トレイごと落とさない)。
            TrayLog.Warn($"管理画面を開けませんでした: {ex}");
            var broken = _window;
            _window = null;
            _viewModel = null;
            TryClose(broken);
            _tray.ShowBalloonTip(4000, "Radirec", "管理画面を開けませんでした(gui.log 参照)", ToolTipIcon.Warning); // W-005
        }
    }

    /// <summary>
    /// 管理画面を開いてホーム(要対応)を出す(保留・取込失敗のトーストのクリック。#237)。閉じていれば開く(開いた画面はホームから
    /// 始まり、要対応を計算する)。開いていれば前面に出し(最小化していれば戻す)、ホームへ移る。
    /// </summary>
    public void ShowHome()
    {
        var open = _viewModel;
        Show();
        // 開いたばかりの画面はホームで、要対応も計算を始めている(もう 1 度頼まない)。Show が開いていた画面を閉じた(失敗)ときは何もしない。
        if (open is not null && ReferenceEquals(open, _viewModel))
            open.ShowHome();
    }
    /// <summary>
    /// UI スレッドの未捕捉例外(WinForms の <c>Application.ThreadException</c> / WPF の <c>Dispatcher.UnhandledException</c>)
    /// の受け口。gui.log に書き、管理画面が開いていればそれだけを閉じてバルーンで知らせる。トレイとワーカーは生かす。
    /// </summary>
    public void OnUnhandledUiException(Exception ex, string source)
    {
        TrayLog.Error($"UI スレッドで捕まえていない例外({source}): {ex}");

        // 閉じる途中の例外でもう一度ここに来たら、入れ子で閉じない(ログだけ残す)。
        if (_closingAfterError || _window is not { } window)
            return;

        _closingAfterError = true;
        try
        {
            _window = null;
            _viewModel = null;
            TryClose(window);
        }
        finally
        {
            _closingAfterError = false;
        }
        _tray.ShowBalloonTip(5000, "Radirec", ClosedAfterErrorBalloon, ToolTipIcon.Warning);
    }

    // 番組ページ(#154)の programs.json の場所。ワーカーがあれば status(設定リロードの反映に追従する)、無ければ起動時に
    // 読めた設定から(トレイの「設定ファイルを開く」と同じ。#143)。設定を読めていなければ null(W-211)。
    // 返す値が null なら programs.json を使わない設定(ProgramsFile が空。W-212)。
    private Func<string?>? ProgramsFileLocator()
    {
        if (_settings.ConfigPath is not { } configPath || (_control is null && _settings.StartupConfig is null))
            return null;

        var control = _control;
        var startup = _settings.StartupConfig;
        return () =>
        {
            if (control is not null)
            {
                var status = control.GetStatus();
                return ConfigPaths.Locate(ConfigFile.Programs, configPath, status.StationsFile, status.ProgramsFile);
            }
            return ConfigPaths.Locate(ConfigFile.Programs, configPath, startup?.StationsFile, startup?.ProgramsFile);
        };
    }

    // ホームの要対応(#155・ADR-0018)。台帳と、ディスクの programs.json / stations.json から計算する(取り込みが読み込んだ設定では
    // なく、「統合する」「分けておく」が書く先と同じもの)。場所は、ワーカーがあれば status(設定リロードに追従。台帳も #196)、無ければ
    // 起動時に読めた設定から。1 回の計算では状態を 1 回だけ読み、3 つの場所を同じ状態から取る(LedgerLocator.Resolve)。
    // 設定を読めていなければ(縮退)null(W-137)。
    private Func<Radirec.Core.Attention.AttentionItems>? AttentionComputer()
    {
        if (_ledgerPath is not { } startupLedger || _settings.ConfigPath is not { } configPath
            || _settings.StartupConfig is not { } startup)
            return null;

        var control = _control;
        return () =>
        {
            var at = LedgerLocator.Resolve(control?.GetStatus(), startupLedger, startup.StationsFile, startup.ProgramsFile);
            return AttentionLoader.Load(
                at.LedgerPath,
                ConfigPaths.Locate(ConfigFile.Stations, configPath, at.StationsFile, at.ProgramsFile),
                ConfigPaths.Locate(ConfigFile.Programs, configPath, at.StationsFile, at.ProgramsFile),
                startup.KnownRecordingKinds);
        };
    }

    // W-508 の確認(#157)。管理画面を持ち主にして出す。既定はキャンセル側(#157 の設計の決定 3)。
    private bool ConfirmApplyUpdate(string message)
    {
        var result = _window is { } owner
            ? System.Windows.MessageBox.Show(owner, message, UpdateTexts.ApplyConfirmCaption,
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
            : System.Windows.MessageBox.Show(message, UpdateTexts.ApplyConfirmCaption,
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        return result == MessageBoxResult.OK;
    }

    private static void TryClose(ManagementWindow? window)
    {
        if (window is null)
            return;
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            // レイアウトの途中など、ディスパッチャの処理が止まっている間の例外だと Close が投げる。
            // そのときは後回しにして閉じる(開いたまま残さない)。
            TrayLog.Warn($"管理画面を閉じるときに例外(後で閉じ直します): {ex}");
            try
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { window.Close(); }
                    catch (Exception retry) { TrayLog.Warn($"管理画面を閉じ直せませんでした: {retry}"); }
                }));
            }
            catch (Exception post)
            {
                TrayLog.Warn($"管理画面を閉じ直す予約に失敗しました: {post}");
            }
        }
    }
}
