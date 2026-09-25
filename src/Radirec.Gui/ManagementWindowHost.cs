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
    private bool _closingAfterError;

    /// <param name="control">取り込みワーカーの制御面。縮退(ワーカーが無い)なら null。</param>
    /// <param name="degradedReason">control が null の理由(W-104 / W-105)。</param>
    /// <param name="ledgerPath">台帳の絶対パス。設定を読めず場所が決まらないなら null(W-150)。</param>
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

            var ledgerPath = _ledgerPath;
            Func<IReadOnlyList<IngestHistoryRow>>? loadHistory = ledgerPath is null ? null : () => IngestHistory.Load(ledgerPath);
            var about = new AboutPageViewModel(
                AppVersion.Of(typeof(ManagementWindowHost).Assembly), _updateSupport, _updater, _updateChecker,
                ConfirmApplyUpdate, TrayLog.Warn);
            var settings = new SettingsPageViewModel(
                _control, _settings.ConfigPath, _settings.StartupConfig, _settings.ConfigError,
                _settings.ToastSetting, new SettingsPageActions(), TrayLog.Warn);
            var viewModel = new ManagementViewModel(
                _control, _degradedReason, loadHistory, new HistoryPathActions(), TrayLog.Warn,
                locateProgramsFile: ProgramsFileLocator(), logInfo: TrayLog.Info, about: about, settings: settings);

            var window = new ManagementWindow(viewModel);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, window))
                    _window = null;
            };
            _window = window;
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            // WPF の初期化や Show の失敗をトレイのクリックのハンドラから漏らさない(トレイごと落とさない)。
            TrayLog.Warn($"管理画面を開けませんでした: {ex}");
            var broken = _window;
            _window = null;
            TryClose(broken);
            _tray.ShowBalloonTip(4000, "Radirec", "管理画面を開けませんでした(gui.log 参照)", ToolTipIcon.Warning); // W-005
        }
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
