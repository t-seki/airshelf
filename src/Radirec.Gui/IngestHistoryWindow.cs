using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using Radirec.Gui.Core;
// WinForms と名前が衝突する WPF 型はエイリアスで固定する(このプロジェクトは WinForms も併用)。
using DataGrid = System.Windows.Controls.DataGrid;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Binding = System.Windows.Data.Binding;
using MessageBox = System.Windows.MessageBox;

namespace Radirec.Gui;

/// <summary>
/// 取込履歴の読み取り専用ウィンドウ(S4 / #66)。台帳の全記録を DataGrid に新しい順で表示する。
/// 列ソート可・編集不可・開いた時点のスナップショット(「更新」で再読込)。トレイ常駐移行(ADR-0012)で
/// 有効化済みの WPF スタックを使う最初のウィンドウ。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IngestHistoryWindow : Window
{
    private readonly Func<IReadOnlyList<IngestHistoryRow>> _load;
    private readonly DataGrid _grid;
    private readonly TextBlock _status;

    public IngestHistoryWindow(Func<IReadOnlyList<IngestHistoryRow>> load)
    {
        _load = load;
        Title = "取込履歴 - Radirec";
        Width = 920;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };
        AddColumn("番組", nameof(IngestHistoryRow.Program), 240);
        AddColumn("放送局", nameof(IngestHistoryRow.Station), 120);
        AddColumn("放送", nameof(IngestHistoryRow.Broadcast), 150);
        AddColumn("取込日時", nameof(IngestHistoryRow.ImportedAt), 140);
        AddColumn("出力先", nameof(IngestHistoryRow.Output), 320);

        var refresh = new Button { Content = "更新", Padding = new Thickness(14, 4, 14, 4) };
        refresh.Click += (_, _) => Reload();
        _status = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        bar.Children.Add(refresh);
        bar.Children.Add(_status);
        DockPanel.SetDock(bar, Dock.Top);

        var root = new DockPanel();
        root.Children.Add(bar);
        root.Children.Add(_grid); // 最後の子が残り領域を埋める
        Content = root;

        Reload();
    }

    private void AddColumn(string header, string path, double width) =>
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path),
            Width = width,
        });

    private void Reload()
    {
        try
        {
            var rows = _load();
            _grid.ItemsSource = rows;
            _status.Text = $"{rows.Count} 件";
        }
        catch (Exception ex)
        {
            _status.Text = "読み込みに失敗しました";
            MessageBox.Show(
                this,
                $"取込履歴の読み込みに失敗しました:\n{ex.Message}",
                "取込履歴", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
