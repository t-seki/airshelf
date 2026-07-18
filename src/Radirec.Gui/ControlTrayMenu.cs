using System.ComponentModel;
using System.Windows.Forms;
using Radirec.Gui.Core;
using Radirec.Hosting.Control;

namespace Radirec.Gui;

/// <summary>
/// トレイの制御メニュー(ADR-0012)。rescan / status / reload を、同一プロセスでホストする
/// 取り込みワーカーの <see cref="IServiceControl"/> を**直接 in-process 呼び出し**で叩く(named pipe は退役)。
/// status はメニューを開いた瞬間(<see cref="ToolStripDropDown.Opening"/>)にスナップショットを読んで表示を
/// 更新する。取り込みワーカー未起動(設定未読込 = <see cref="IServiceControl"/> が null)のときは
/// rescan/reload をグレーアウトし「取り込み停止中」を表示する縮退にする。
/// </summary>
internal sealed class ControlTrayMenu
{
    private readonly NotifyIcon _tray;
    private readonly IServiceControl? _control;
    private readonly string? _degradedReason;
    private readonly string? _ledgerPath;
    private readonly UpdateService? _update;
    private readonly UpdateCoordinator? _updateCoordinator;
    private readonly Func<Task>? _drainWorker; // 更新適用前にワーカーを graceful 停止(S6 レビュー #0)
    private readonly ToolStripMenuItem _rescan;
    private readonly ToolStripMenuItem _reload;
    private readonly ToolStripMenuItem _statusLine;
    private readonly ToolStripMenuItem _lastScanLine;
    private readonly ToolStripMenuItem? _applyUpdate; // 更新保留時のみ表示(S6)
    private IngestHistoryWindow? _historyWindow;

    /// <param name="control">取り込みワーカーの制御面。縮退(未起動)時は null。</param>
    /// <param name="degradedReason">control==null の理由(縮退メニューに表示)。healthy 時は null。</param>
    /// <param name="ledgerPath">台帳の絶対パス(取込履歴 S4 用)。設定未読込なら null で履歴項目を出さない。</param>
    /// <param name="update">自己更新サービス(S6)。null / 未インストール(dev)なら更新項目を出さない。</param>
    /// <param name="updateCoordinator">手動チェック用(S6)。</param>
    /// <param name="drainWorker">更新適用直前にワーカーを graceful 停止するコールバック(S6)。</param>
    public ControlTrayMenu(
        NotifyIcon tray, IServiceControl? control, string? degradedReason = null, string? ledgerPath = null,
        UpdateService? update = null, UpdateCoordinator? updateCoordinator = null, Func<Task>? drainWorker = null)
    {
        _tray = tray;
        _control = control;
        _degradedReason = degradedReason;
        _ledgerPath = ledgerPath;
        _update = update?.IsInstalled == true ? update : null; // dev/portable では更新 UI を出さない
        _updateCoordinator = updateCoordinator;
        _drainWorker = drainWorker;

        _rescan = new ToolStripMenuItem("今すぐ再スキャン", image: null, OnRescan);
        _reload = new ToolStripMenuItem("設定リロード…", image: null, OnReload);

        // 状態表示は非活性のラベル項目(クリック不可)。Opening で内容を差し替える。
        _statusLine = new ToolStripMenuItem("状態: 未確認") { Enabled = false };
        _lastScanLine = new ToolStripMenuItem("最終スキャン: —") { Enabled = false };

        // 版情報(S1 / #63)。クリック不可のヘッダー。CI がタグから注入した版、ローカルは 0.0.0-dev。
        var versionLine = new ToolStripMenuItem($"Radirec {AppVersion.Of(typeof(ControlTrayMenu).Assembly)}")
        {
            Enabled = false,
        };

        Menu = new ContextMenuStrip();
        Menu.Items.Add(versionLine);
        Menu.Items.Add(new ToolStripSeparator());
        Menu.Items.Add(_rescan);
        Menu.Items.Add(_reload);
        Menu.Items.Add(new ToolStripSeparator());
        Menu.Items.Add(_statusLine);
        Menu.Items.Add(_lastScanLine);
        Menu.Items.Add(new ToolStripSeparator());
        // 取込履歴(S4 / #66)。設定が読めていれば台帳を参照できる(ワーカー起動失敗の縮退でも過去分は見える)。
        if (_ledgerPath is not null)
            Menu.Items.Add("取込履歴…", image: null, OnHistory);
        // 更新(S6 / #68)。Velopack インストール時のみ。適用項目は保留中だけ表示する。
        if (_update is not null)
        {
            Menu.Items.Add(new ToolStripSeparator());
            Menu.Items.Add("今すぐ更新を確認", image: null, OnCheckUpdate);
            _applyUpdate = new ToolStripMenuItem("更新を適用して再起動", image: null, OnApplyUpdate) { Visible = false };
            Menu.Items.Add(_applyUpdate);
        }
        AddSeparatorIfNeeded(); // 直前が separator(履歴も更新も無い縮退時)なら足さない(二重線防止)
        // 縮退(設定未読込 / ワーカー起動失敗)時は、設定修正後にプロセスごと再起動して復帰できる導線を出す
        // (control==null は復旧経路が無く、再起動しないと取り込みが始まらないため)。
        if (_control is null)
            Menu.Items.Add("再読み込みして再起動", image: null, (_, _) => Application.Restart());
        Menu.Items.Add("終了", image: null, (_, _) => Application.Exit());

        Menu.Opening += OnOpening;
    }

    public ContextMenuStrip Menu { get; }

    // 末尾が既に区切り線なら足さない(オプション項目が無い縮退時の二重線を防ぐ)。
    private void AddSeparatorIfNeeded()
    {
        if (Menu.Items.Count > 0 && Menu.Items[^1] is not ToolStripSeparator)
            Menu.Items.Add(new ToolStripSeparator());
    }

    private void OnOpening(object? sender, CancelEventArgs e)
    {
        RefreshUpdateItem(); // 直近のチェックで見つかった保留更新を反映(S6)

        if (_control is null)
        {
            // 取り込みワーカー未起動(設定未読込 or 起動失敗)。制御は不可、理由を明示する。
            _rescan.Enabled = false;
            _reload.Enabled = false;
            _statusLine.Text = $"状態: 取り込み停止中({_degradedReason ?? "未起動"})";
            _lastScanLine.Text = "最終スキャン: —";
            return;
        }

        // in-process なのでスナップショットを直接・即時に読める(pipe 往復・非同期は不要)。
        ApplyReachable(_control.GetStatus());
    }

    private void ApplyReachable(ServiceStatusSnapshot status)
    {
        // ワーカーが異常終了してもトレイ(メッセージループ)は生存しうる。その場合「稼働中」と偽らず、
        // 停止を明示して rescan/reload を伏せる(誤って「動いている」と信じさせない。ADR-0012)。
        if (!status.Running)
        {
            _rescan.Enabled = false;
            _reload.Enabled = false;
            _statusLine.Text = "状態: 停止(取り込みが異常終了。gui.log 参照 → 再起動)";
            _lastScanLine.Text = "最終スキャン: —";
            return;
        }

        _rescan.Enabled = true;
        _reload.Enabled = true;

        _statusLine.Text = status.Watching ? "状態: 稼働中(監視中)" : "状態: 稼働中(定期スキャンのみ)";

        if (status.LastScanCompletedUtc is { } completed)
        {
            var local = completed.ToLocalTime().ToString("M/d HH:mm");
            var trigger = string.IsNullOrEmpty(status.LastScanTrigger) ? "" : $" {status.LastScanTrigger}";
            var failed = status.LastFailed > 0 ? $"・失敗 {status.LastFailed}" : "";
            _lastScanLine.Text = $"最終スキャン: {local}{trigger}(取込 {status.LastImported}{failed})";
        }
        else
        {
            _lastScanLine.Text = "最終スキャン: 未走査";
        }
    }

    private void OnRescan(object? sender, EventArgs e)
    {
        // 要求チャネルへ coalesce される冪等操作。即座に返る(スキャン完了は待たない)。
        _control?.RequestRescan();
        _tray.ShowBalloonTip(3000, "Radirec", "手動再スキャンを要求しました", ToolTipIcon.Info);
    }

    private void OnReload(object? sender, EventArgs e)
    {
        if (_control is null)
            return;

        var confirm = MessageBox.Show(
            "設定を再読み込みします。取り込みは一時停止し、再構築されます。よろしいですか?",
            "設定リロード",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
            return;

        // 読込+構文検証だけ同期実行して受理/拒否が即返る(実グラフ差し替えは在中スキャン完了後)。
        var outcome = _control.RequestReload();
        if (outcome.Accepted)
            _tray.ShowBalloonTip(3000, "Radirec", "設定リロードを受理しました(反映は取込完了後)", ToolTipIcon.Info);
        else
            _tray.ShowBalloonTip(5000, "Radirec", $"設定リロードは拒否されました: {outcome.Error}", ToolTipIcon.Warning);
    }

    private void OnHistory(object? sender, EventArgs e)
    {
        if (_ledgerPath is null)
            return;

        try
        {
            // 既に開いていれば前面化する(複数枚を防ぐ)。最小化されていれば通常表示へ戻してから前面化する
            // (Activate=SetForegroundWindow は最小化を解除しないため、クリックが無反応に見えるのを防ぐ)。
            if (_historyWindow is { IsLoaded: true })
            {
                if (_historyWindow.WindowState == System.Windows.WindowState.Minimized)
                    _historyWindow.WindowState = System.Windows.WindowState.Normal;
                _historyWindow.Activate();
                return;
            }

            var window = new IngestHistoryWindow(() => IngestHistory.Load(_ledgerPath));
            window.Closed += (_, _) => _historyWindow = null;
            _historyWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            // このプロセス初の WPF ウィンドウ。WPF プレゼンテーション初期化や Show の失敗が WinForms の
            // クリックハンドラを抜けて未処理例外(ThreadException)になり、トレイ全体を巻き添えに落とすのを防ぐ。
            // (Load 失敗は窓内の Reload が別途握るので、ここは主に WPF 初期化・Show の失敗を対象にする。)
            _historyWindow = null;
            TrayLog.Warn($"取込履歴ウィンドウを開けませんでした: {ex}");
            _tray.ShowBalloonTip(4000, "Radirec", "取込履歴を開けませんでした(gui.log 参照)", ToolTipIcon.Warning);
        }
    }

    private async void OnCheckUpdate(object? sender, EventArgs e)
    {
        if (_updateCoordinator is null)
            return;
        await _updateCoordinator.CheckAndNotifyAsync(manual: true); // 結果はバルーンで返る
        RefreshUpdateItem();
    }

    private async void OnApplyUpdate(object? sender, EventArgs e)
    {
        if (_update is null)
            return;

        // メニューを開いてから確定までの間に、自動チェックが保留更新を取り下げる(release 撤回等)ことがある。
        // その場合は無反応にせず知らせて項目を隠す。
        var version = _update.PendingVersion;
        if (version is null)
        {
            _tray.ShowBalloonTip(3000, "Radirec", "適用できる更新はありません(最新か、取り下げられました)。", ToolTipIcon.Info);
            RefreshUpdateItem();
            return;
        }

        var confirm = MessageBox.Show(
            $"更新 v{version} を適用してアプリを再起動します。取り込み中の走査は完了を待ってから安全に再起動します。よろしいですか?",
            "更新の適用",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (confirm != DialogResult.OK)
            return;

        try
        {
            // DL → ワーカーを graceful 停止(在中スキャン完了・SQLite クローズ)→ 適用・再起動(戻らない)。
            await _update.ApplyAndRestartAsync(_drainWorker);
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"更新の適用に失敗しました: {ex}");
            _tray.ShowBalloonTip(4000, "Radirec", "更新の適用に失敗しました(gui.log 参照)。", ToolTipIcon.Warning);
        }
    }

    // 保留中の更新があれば「更新 vX を適用…」項目を出す。無ければ隠す。Opening と手動チェック後に呼ぶ。
    private void RefreshUpdateItem()
    {
        if (_applyUpdate is null || _update is null)
            return;

        var version = _update.PendingVersion;
        if (version is null)
        {
            _applyUpdate.Visible = false;
        }
        else
        {
            _applyUpdate.Text = $"更新 v{version} を適用して再起動";
            _applyUpdate.Visible = true;
        }
    }
}
