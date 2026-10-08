using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Airshelf.Gui.Core;
using Airshelf.Gui.Core.Updates;
using Airshelf.Hosting.Control;

namespace Airshelf.Gui;

/// <summary>
/// トレイの制御メニュー(ADR-0012)。rescan / status を、同一プロセスでホストする取り込みワーカーの
/// <see cref="IServiceControl"/> を**直接 in-process 呼び出し**で叩く(トレイのメニューは pipe を通らない。CLI の
/// status / reload は #179 の制御チャネル〈named pipe。<c>ControlPipeServer</c>〉で同じ IServiceControl に届く)。
/// status はメニューを開いた瞬間(<see cref="ToolStripDropDown.Opening"/>)にスナップショットを読んで表示を
/// 更新する。取り込みワーカー未起動(設定未読込 = <see cref="IServiceControl"/> が null)のときは
/// 「今すぐ再スキャン」をグレーアウトし「取り込み停止中」を表示する縮退にする。
/// 項目は requirements §10.3 の「トレイに残すもの」だけ:版 / Radirec を開く / 今すぐ再スキャン / 状態・最終スキャン /
/// 更新を適用して再起動(保留中のみ)/ 再読み込みして再起動(縮退時のみ)/ 終了。ほかは管理画面へ移した:
/// 取込履歴(#151)、設定リロードと設定ファイルを開く(設定ページ。#152)、統合設定の再適用(番組ページの「保存して適用」。
/// #154。再適用の確認ダイアログから programs.json に追記する流れ〈#144〉も一緒に退役した。要対応〈#155〉が引き継ぐ)、
/// 今すぐ更新を確認(バージョン情報ページ。#157)。
/// 「Radirec を開く」(#151)は管理画面を開く既定の項目(太字。アイコンのダブルクリックでも開く)で、縮退時も選べる。
/// </summary>
internal sealed class ControlTrayMenu
{
    private readonly NotifyIcon _tray;
    private readonly IServiceControl? _control;
    private readonly string? _degradedReason;
    private readonly UpdateService? _update;
    private readonly Func<Task>? _drainWorker; // 更新適用前にワーカーを graceful 停止(S6 レビュー #0)
    private readonly ToolStripMenuItem _rescan;
    private readonly ToolStripMenuItem _statusLine;
    private readonly ToolStripMenuItem _lastScanLine;
    private readonly ToolStripMenuItem? _applyUpdate; // 更新保留時のみ表示(S6)
    private readonly ToolStripSeparator? _updateSeparator; // _applyUpdate の上の区切り線。同じく保留時のみ(#157)

    /// <param name="openManagement">管理画面を開く(#151)。「Radirec を開く」で呼ぶ。</param>
    /// <param name="control">取り込みワーカーの制御面。縮退(未起動)時は null。</param>
    /// <param name="degradedReason">control==null の理由(縮退メニューに表示)。healthy 時は null。</param>
    /// <param name="update">自己更新サービス(S6)。null / 未インストール(dev)なら更新項目を出さない。</param>
    /// <param name="drainWorker">更新適用直前にワーカーを graceful 停止するコールバック(S6)。</param>
    public ControlTrayMenu(
        NotifyIcon tray, Action openManagement, IServiceControl? control, string? degradedReason = null,
        UpdateService? update = null, Func<Task>? drainWorker = null)
    {
        _tray = tray;
        _control = control;
        _degradedReason = degradedReason;
        _update = update?.IsInstalled == true ? update : null; // dev/portable では更新 UI を出さない
        _drainWorker = drainWorker;

        _rescan = new ToolStripMenuItem("今すぐ再スキャン", image: null, OnRescan);

        // 状態表示は非活性のラベル項目(クリック不可)。Opening で内容を差し替える。
        _statusLine = new ToolStripMenuItem("状態: 未確認") { Enabled = false };
        _lastScanLine = new ToolStripMenuItem("最終スキャン: —") { Enabled = false };

        // 版情報(S1 / #63)。クリック不可のヘッダー。CI がタグから注入した版、ローカルは 0.0.0-dev。
        var versionLine = new ToolStripMenuItem($"Radirec {AppVersion.Of(typeof(ControlTrayMenu).Assembly)}")
        {
            Enabled = false,
        };

        Menu = new ContextMenuStrip();

        // 管理画面を開く(#151・W-602)。既定の項目なので太字にする(アイコンのダブルクリックでも開く。Program.cs)。
        // 縮退時も選べる(管理画面は縮退の状態を見せる)。
        var open = new ToolStripMenuItem("Radirec を開く", image: null, (_, _) => openManagement());
        open.Font = new Font(open.Font, FontStyle.Bold);

        Menu.Items.Add(versionLine);
        Menu.Items.Add(new ToolStripSeparator());
        Menu.Items.Add(open); // モックの並び(W-601 / 区切り / W-602 / W-603)
        Menu.Items.Add(_rescan);
        Menu.Items.Add(new ToolStripSeparator());
        Menu.Items.Add(_statusLine);
        Menu.Items.Add(_lastScanLine);
        // 更新(S6 / #68)。Velopack インストール時のみ。適用項目(W-605)は保留中だけ、上の区切り線ごと表示する。
        // 「今すぐ更新を確認」は管理画面のバージョン情報ページへ移した(#157)。
        if (_update is not null)
        {
            _updateSeparator = new ToolStripSeparator { Visible = false };
            Menu.Items.Add(_updateSeparator);
            _applyUpdate = new ToolStripMenuItem("更新を適用して再起動", image: null, OnApplyUpdate) { Visible = false };
            Menu.Items.Add(_applyUpdate);
        }
        Menu.Items.Add(new ToolStripSeparator()); // 直前は最終スキャンの行か更新の項目(区切り線にはならない)
        // 縮退(設定未読込 / ワーカー起動失敗)時は、設定修正後にプロセスごと再起動して復帰できる導線を出す
        // (control==null は復旧経路が無く、再起動しないと取り込みが始まらないため)。
        if (_control is null)
            Menu.Items.Add("再読み込みして再起動", image: null, (_, _) => Application.Restart());
        Menu.Items.Add("終了", image: null, (_, _) => Application.Exit());

        Menu.Opening += OnOpening;
    }

    public ContextMenuStrip Menu { get; }

    private void OnOpening(object? sender, CancelEventArgs e)
    {
        RefreshUpdateItem(); // 直近のチェックで見つかった保留更新を反映(S6)

        // 取り込みワーカー未起動(設定未読込 or 起動失敗)なら理由を、あれば in-process のスナップショットを
        // 直接・即時に読んで出す(W-101〜W-108・W-153。文言は管理画面のホームと共有する)。ワーカーが異常終了しても
        // トレイ(メッセージループ)は生存しうるが、その場合は「稼働中」と偽らず停止を出す(ADR-0012)。
        var view = _control is null
            ? ServiceStatusText.Stopped(_degradedReason)
            : ServiceStatusText.From(_control.GetStatus());
        _statusLine.Text = view.Status;
        _lastScanLine.Text = view.LastScan;

        // 取り込みが止まっていれば「今すぐ再スキャン」を伏せる。
        _rescan.Enabled = view.CanControl;
    }

    private void OnRescan(object? sender, EventArgs e)
    {
        // 要求チャネルへ coalesce される冪等操作。即座に返る(スキャン完了は待たない)。
        _control?.RequestRescan();
        _tray.ShowBalloonTip(3000, "Radirec", "手動再スキャンを要求しました", ToolTipIcon.Info);
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
            _tray.ShowBalloonTip(3000, "Radirec", UpdateTexts.NothingToApply, ToolTipIcon.Info); // W-509
            RefreshUpdateItem();
            return;
        }

        // W-508。既定はキャンセル側(再起動を Enter の押し間違いで始めない。管理画面の確認と揃える。#157 の設計の決定 3)。
        var confirm = MessageBox.Show(
            UpdateTexts.ApplyConfirm(version),
            UpdateTexts.ApplyConfirmCaption,
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
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
            _tray.ShowBalloonTip(4000, "Radirec", UpdateTexts.ApplyFailed, ToolTipIcon.Warning); // W-509
        }
    }

    // 保留中の更新があれば「更新 vX を適用…」項目(W-605)を上の区切り線ごと出す。無ければ隠す。Opening で呼ぶ。
    private void RefreshUpdateItem()
    {
        if (_applyUpdate is null || _update is null)
            return;

        var version = _update.PendingVersion;
        if (version is not null)
            _applyUpdate.Text = UpdateTexts.ApplyButton(version);
        _applyUpdate.Visible = version is not null;
        if (_updateSeparator is not null)
            _updateSeparator.Visible = version is not null;
    }
}
