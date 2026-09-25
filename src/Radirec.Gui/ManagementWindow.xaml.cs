using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Radirec.Gui.Core.Management;
// System.Windows.Input は名前空間ごと入れると Cursor / MouseEventArgs 等が WinForms と衝突するため、
// 使う型だけをエイリアスで持ち込む(このプロジェクトは WinForms も併用)。
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Radirec.Gui;

/// <summary>
/// 管理画面のウィンドウ(requirements §10.3・#151)。中身は <see cref="ManagementViewModel"/> に結ぶだけの薄いシェルで、
/// ここに置くのは ViewModel では書けないもの(タイマーと、取込履歴の行のマウス・キー操作)だけ。
/// 開いている間だけ 2 秒ごとに状態を読み直す(<see cref="ManagementViewModel.Tick"/>)。
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ManagementWindow : Window
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly ManagementViewModel _viewModel;
    private readonly DispatcherTimer _timer;

    public ManagementWindow(ManagementViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Tick の例外は ViewModel の中で握る(gui.log に書く)。ここから漏れたら UI スレッドの最後の網が
        // 管理画面を閉じる(Program.cs)。
        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => _viewModel.Tick(), Dispatcher);
        Closed += (_, _) => _timer.Stop();
    }

    // 行のダブルクリック・Enter で出力先を開く(旧取込履歴ウィンドウと同じ。#87)。
    private void OnHistoryRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        RevealSelectedOutput();
        e.Handled = true;
    }

    private void OnHistoryGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        RevealSelectedOutput();
        e.Handled = true; // DataGrid 既定の「次の行へ移動」を抑える
    }

    private void OnHistoryRowRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右クリックのメニューは「選んでいる行」に対して働くので、開く前にクリックされた行を選ぶ
        // (右クリックで選択が動くかは DataGrid の既定に依るため、ここで確定させる)。
        // e.Handled は立てない。メニューは通常どおり開く必要がある。
        if (sender is DataGridRow row)
            row.IsSelected = true;
    }

    private void RevealSelectedOutput()
    {
        var command = _viewModel.History.RevealOutputCommand;
        if (command.CanExecute(null))
            command.Execute(null);
    }
}
