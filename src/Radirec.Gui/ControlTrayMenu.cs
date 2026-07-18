using System.ComponentModel;
using System.Windows.Forms;
using Radirec.Gui.Core;
using Radirec.Gui.Core.Control;

namespace Radirec.Gui;

/// <summary>
/// トレイの制御チャネル窓口(ADR-0010 / 軸B)。rescan / status / reload をメニューから叩く。
/// status はメニューを開いた瞬間(<see cref="ToolStripDropDown.Opening"/>)に 1 回問い合わせ、結果で
/// 表示を書き換える(常時ポーリング=B2 はしない)。パイプ呼び出しは <c>async void</c> ハンドラで行い、
/// <c>await</c> 継続が UI スレッドへ戻る性質でメニューを固めずに項目を更新する。サービス未達
/// (<see cref="ControlUnavailableException"/>)は「サービス停止中」表示 + rescan/reload グレーアウトへ縮退し、
/// エラーダイアログは出さない(サービス未起動は正常な状態の 1 つ)。
/// </summary>
internal sealed class ControlTrayMenu
{
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _rescan;
    private readonly ToolStripMenuItem _reload;
    private readonly ToolStripMenuItem _statusLine;
    private readonly ToolStripMenuItem _lastScanLine;
    private CancellationTokenSource? _statusCts;
    // rescan/reload の多重発火防止。全ハンドラは UI スレッド上で直列化されるので単純な bool で足りる。
    private bool _commandInFlight;

    public ControlTrayMenu(NotifyIcon tray)
    {
        _tray = tray;

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
        Menu.Items.Add("終了", image: null, (_, _) => Application.Exit());

        Menu.Opening += OnOpening;
    }

    public ContextMenuStrip Menu { get; }

    private async void OnOpening(object? sender, CancelEventArgs e)
    {
        // 前回の開き掛け問い合わせが残っていればキャンセルして最新だけを反映する。ここで Dispose は
        // しない — in-flight な SendAsync が linked token 経由で参照している窓で ObjectDisposedException を
        // 招きうるため。CancelAfter 無しの CTS なので未 Dispose のコストは無視できる(GC 回収)。
        _statusCts?.Cancel();
        var cts = _statusCts = new CancellationTokenSource();

        _statusLine.Text = "状態: 確認中…";
        _lastScanLine.Text = "最終スキャン: —";

        try
        {
            var status = await ControlClient.GetStatusAsync(cancellationToken: cts.Token);
            if (cts != _statusCts)
                return; // 後続の Opening に追い抜かれた。古い結果で上書きしない。
            ApplyReachable(status);
        }
        catch (OperationCanceledException)
        {
            // 次の Opening に追い抜かれた。何もしない。
        }
        catch (ControlUnavailableException)
        {
            // キャンセル(supersession)が IOException 経由で ControlUnavailable に正規化される経路がある。
            // 追い抜かれていたら「サービス停止中」で新しい稼働中表示を潰さない。
            if (cts != _statusCts)
                return;
            ApplyUnavailable();
        }
    }

    private void ApplyReachable(ServiceStatusSnapshot status)
    {
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

    private void ApplyUnavailable()
    {
        _rescan.Enabled = false;
        _reload.Enabled = false;
        _statusLine.Text = "状態: サービス停止中";
        _lastScanLine.Text = "最終スキャン: —";
    }

    private async void OnRescan(object? sender, EventArgs e)
    {
        if (_commandInFlight)
            return;
        _commandInFlight = true;
        try
        {
            await ControlClient.RequestRescanAsync();
            _tray.ShowBalloonTip(3000, "Radirec", "手動再スキャンを要求しました", ToolTipIcon.Info);
        }
        catch (ControlUnavailableException ex)
        {
            NotifyUnavailable(ex);
        }
        finally
        {
            _commandInFlight = false;
        }
    }

    private async void OnReload(object? sender, EventArgs e)
    {
        if (_commandInFlight)
            return;

        var confirm = MessageBox.Show(
            "設定を再読み込みします。サービスが実質再起動されます(取込は一時停止し、再構築されます)。よろしいですか?",
            "設定リロード",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
            return;

        _commandInFlight = true;
        try
        {
            var outcome = await ControlClient.RequestReloadAsync();
            if (outcome.Accepted)
                _tray.ShowBalloonTip(3000, "Radirec", "設定リロードを受理しました(反映は取込完了後)", ToolTipIcon.Info);
            else
                _tray.ShowBalloonTip(5000, "Radirec", $"設定リロードは拒否されました: {outcome.Error}", ToolTipIcon.Warning);
        }
        catch (ControlUnavailableException ex)
        {
            NotifyUnavailable(ex);
        }
        finally
        {
            _commandInFlight = false;
        }
    }

    private void NotifyUnavailable(ControlUnavailableException ex) =>
        _tray.ShowBalloonTip(4000, "Radirec", ex.Message, ToolTipIcon.Warning);
}
