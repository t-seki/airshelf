using System.Runtime.Versioning;
using System.Windows.Controls;
using Radirec.Gui.Core.Management;
// System.Windows.Input は名前空間ごと入れると WinForms と衝突するため、使う型だけをエイリアスで持ち込む。
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Radirec.Gui.Pages;

/// <summary>
/// 管理画面の番組ページ(#154)。中身は <see cref="ProgramsPageViewModel"/> に結ぶだけの薄いシェルで、ここに置くのは
/// ViewModel では書けない行のダブルクリック(編集を開く)だけ。
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ProgramsPage : UserControl
{
    public ProgramsPage()
    {
        InitializeComponent();
    }

    private ProgramsPageViewModel? ViewModel => DataContext as ProgramsPageViewModel;

    // DataGrid の MouseDoubleClick は見出し(列幅の自動調整)でも起きるので、行の上のときだけ編集を開く(取込履歴と同じ)。
    // 行は RowStyle の EventSetter ではなく DataGridRowHit で探す(RowStyle は Fluent の行のスタイルを上書きするため。#171)。
    private void OnProgramGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataGridRowHit.RowOf(e.OriginalSource) is { Item: ProgramEntryRow row } && ViewModel is { } vm)
        {
            vm.SelectedProgram = row;
            if (vm.EditProgramCommand.CanExecute(null))
                vm.EditProgramCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnKeepGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataGridRowHit.RowOf(e.OriginalSource) is { Item: KeepSeparateRow row } && ViewModel is { } vm)
        {
            vm.SelectedKeep = row;
            if (vm.EditKeepCommand.CanExecute(null))
                vm.EditKeepCommand.Execute(null);
            e.Handled = true;
        }
    }
}
