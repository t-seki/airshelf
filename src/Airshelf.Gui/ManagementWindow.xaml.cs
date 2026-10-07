using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Airshelf.Gui.Core.Management;
// System.Windows.Input は名前空間ごと入れると Cursor / MouseEventArgs 等が WinForms と衝突するため、
// 使う型だけをエイリアスで持ち込む(このプロジェクトは WinForms も併用)。
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Airshelf.Gui;

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
        FollowWindowsAppMode();

        // Tick の例外は ViewModel の中で握る(gui.log に書く)。ここから漏れたら UI スレッドの最後の網が
        // 管理画面を閉じる(Program.cs)。
        _timer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, (_, _) => _viewModel.Tick(), Dispatcher);
        Closed += (_, _) => _timer.Stop();
    }

    // Windows のアプリのモード(ライト / ダーク)に追従する(#171)。WPF の Fluent テーマをこのウィンドウにだけ入れる。
    // トレイは WinForms の Application.Run の上でこのウィンドウを開くので、WPF の Application のインスタンスが無い。
    // Application.ThemeMode は使えないが、Window.ThemeMode はウィンドウの Resources に Fluent の辞書を入れ、Windows の
    // 設定の変更(WM_SETTINGCHANGE)でも、Application を通らずにこのウィンドウの色を当て直す(dotnet/wpf release/10.0 の
    // ThemeManager.OnSystemThemeChanged の else の枝と SystemResources の WM_SETTINGCHANGE)。
    // Window.ThemeMode は実験的 API(WPF0001)なので、警告はここだけで抑える。
    // XAML の色は Fluent の辞書のキー(TextFillColorSecondaryBrush など)を DynamicResource で引く。
    // SystemColors のキーは Fluent が上書きしないので、ダークでもクラシックの色のままになるため使わない。
    private void FollowWindowsAppMode()
    {
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
    }

    // 行のダブルクリック・Enter で出力先を開く(旧取込履歴ウィンドウと同じ。#87)。DataGrid の MouseDoubleClick は見出し
    // (列幅の自動調整)でも起きるので、行の上のときだけ開く(DataGridRowHit)。
    private void OnHistoryGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataGridRowHit.RowOf(e.OriginalSource) is null)
            return;

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

    private void OnHistoryGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右クリックのメニューは「選んでいる行」に対して働くので、開く前にクリックされた行を選ぶ
        // (右クリックで選択が動くかは DataGrid の既定に依るため、ここで確定させる)。
        // e.Handled は立てない。メニューは通常どおり開く必要がある。
        if (DataGridRowHit.RowOf(e.OriginalSource) is { } row)
            row.IsSelected = true;
    }

    private void RevealSelectedOutput()
    {
        var command = _viewModel.History.RevealOutputCommand;
        if (command.CanExecute(null))
            command.Execute(null);
    }
}
