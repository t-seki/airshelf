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
    private readonly ToolStripMenuItem _rescan;
    private readonly ToolStripMenuItem _reload;
    private readonly ToolStripMenuItem _statusLine;
    private readonly ToolStripMenuItem _lastScanLine;

    /// <param name="control">取り込みワーカーの制御面。縮退(未起動)時は null。</param>
    /// <param name="degradedReason">control==null の理由(縮退メニューに表示)。healthy 時は null。</param>
    public ControlTrayMenu(NotifyIcon tray, IServiceControl? control, string? degradedReason = null)
    {
        _tray = tray;
        _control = control;
        _degradedReason = degradedReason;

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
}
