using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using Radirec.Gui.Core;
// WinForms と名前が衝突する WPF 型はエイリアスで固定する(このプロジェクトは WinForms も併用)。
using DataGrid = System.Windows.Controls.DataGrid;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Clipboard = System.Windows.Clipboard;
// System.Windows.Input は名前空間ごと入れると Cursor / MouseEventArgs 等が WinForms と衝突するため、
// 使う型だけをエイリアスで持ち込む。
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonEventHandler = System.Windows.Input.MouseButtonEventHandler;

namespace Radirec.Gui;

/// <summary>
/// 取込履歴の読み取り専用ウィンドウ(S4 / #66)。台帳の全記録を DataGrid に新しい順で表示する。
/// 列ソート可・編集不可・開いた時点のスナップショット(「更新」で再読込)。トレイ常駐移行(ADR-0012)で
/// 有効化済みの WPF スタックを使う最初のウィンドウ。
/// #87 で「開く」導線を追加: グリッドにはファイル名だけを出し、フルパスは ToolTip と下部の詳細ペインで
/// 見せる。右クリック / ダブルクリック / Enter から出力先・取込元をエクスプローラーで開ける。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IngestHistoryWindow : Window
{
    private readonly Func<IReadOnlyList<IngestHistoryRow>> _load;
    private readonly DataGrid _grid;
    private readonly TextBlock _count;
    private readonly TextBlock _action;
    private readonly TextBox _outputPathBox;
    private readonly TextBox _sourcePathBox;
    private readonly MenuItem _openOutput;
    private readonly MenuItem _openSource;
    private readonly MenuItem _copyOutput;
    private readonly MenuItem _copySource;

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
        AddColumn("番組", nameof(IngestHistoryRow.Program), new DataGridLength(240));
        AddColumn("放送局", nameof(IngestHistoryRow.Station), new DataGridLength(120));
        AddColumn("放送", nameof(IngestHistoryRow.Broadcast), new DataGridLength(150));
        AddColumn("取込日時", nameof(IngestHistoryRow.ImportedAt), new DataGridLength(140));
        // 出力先はファイル名だけを出し、残り幅を全部与える(#87)。フルパスは ToolTip / 詳細ペインで。
        AddColumn(
            "ファイル",
            nameof(IngestHistoryRow.FileName),
            new DataGridLength(1, DataGridLengthUnitType.Star),
            tooltipPath: nameof(IngestHistoryRow.OutputPath));

        _openOutput = new MenuItem { Header = "出力先をエクスプローラーで開く" };
        _openOutput.Click += (_, _) => Reveal(output: true);
        _openSource = new MenuItem { Header = "取込元をエクスプローラーで開く" };
        _openSource.Click += (_, _) => Reveal(output: false);
        _copyOutput = new MenuItem { Header = "出力先のパスをコピー" };
        _copyOutput.Click += (_, _) => CopyPath(output: true);
        _copySource = new MenuItem { Header = "取込元のパスをコピー" };
        _copySource.Click += (_, _) => CopyPath(output: false);

        var menu = new ContextMenu();
        menu.Items.Add(_openOutput);
        menu.Items.Add(_openSource);
        menu.Items.Add(new Separator());
        menu.Items.Add(_copyOutput);
        menu.Items.Add(_copySource);
        menu.Opened += (_, _) => RefreshMenuState();
        _grid.ContextMenu = menu;

        // 行のダブルクリックで出力先を開く。DataGrid 自体の MouseDoubleClick はヘッダー(列幅の自動調整)
        // でも発火するため、行に限定するために RowStyle 経由で拾う。
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new EventSetter(
            System.Windows.Controls.Control.MouseDoubleClickEvent,
            new MouseButtonEventHandler(OnRowDoubleClick)));
        rowStyle.Setters.Add(new EventSetter(
            UIElement.PreviewMouseRightButtonDownEvent,
            new MouseButtonEventHandler(OnRowRightButtonDown)));
        _grid.RowStyle = rowStyle;

        _grid.KeyDown += OnGridKeyDown;
        _grid.SelectionChanged += (_, _) => RefreshDetail();

        var refresh = new Button { Content = "更新", Padding = new Thickness(14, 4, 14, 4) };
        refresh.Click += (_, _) => Reload();
        _count = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        // 「開く / コピー」の結果はここに出す(件数表示を潰さない)。
        _action = new TextBlock
        {
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // StackPanel ではなく DockPanel にする。横積みだと _action に無限幅が与えられて
        // TextTrimming が効かず、長いパスを含むメッセージが窓の外へはみ出すため。
        var bar = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
        DockPanel.SetDock(refresh, Dock.Left);
        DockPanel.SetDock(_count, Dock.Left);
        bar.Children.Add(refresh);
        bar.Children.Add(_count);
        bar.Children.Add(_action); // 残り幅を埋め、溢れたら省略される
        DockPanel.SetDock(bar, Dock.Top);

        _outputPathBox = CreatePathBox();
        _sourcePathBox = CreatePathBox();
        var detail = BuildDetailPane();
        DockPanel.SetDock(detail, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(bar);
        root.Children.Add(detail);
        root.Children.Add(_grid); // 最後の子が残り領域を埋める
        Content = root;

        Reload();
    }

    private void AddColumn(string header, string path, DataGridLength width, string? tooltipPath = null)
    {
        var column = new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path),
            Width = width,
        };

        if (tooltipPath is not null)
        {
            // DataGridTextColumn のセルは TextBlock。ToolTip にフルパスを出す(列は切り詰められるため)。
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(ToolTipProperty, new Binding(tooltipPath)));
            column.ElementStyle = style;
        }

        _grid.Columns.Add(column);
    }

    // 選択・コピーできる読み取り専用テキスト。パスは長いので折り返して全文を見せる。
    private static TextBox CreatePathBox() => new()
    {
        IsReadOnly = true,
        IsReadOnlyCaretVisible = false,
        BorderThickness = new Thickness(0),
        Background = System.Windows.Media.Brushes.Transparent,
        TextWrapping = TextWrapping.Wrap,
        Padding = new Thickness(0),
    };

    private UIElement BuildDetailPane()
    {
        var grid = new Grid { Margin = new Thickness(10, 6, 10, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddDetailRow(grid, row: 0, label: "出力先", value: _outputPathBox);
        AddDetailRow(grid, row: 1, label: "取込元", value: _sourcePathBox);

        return new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = System.Windows.SystemColors.ControlLightBrush,
            Child = grid,
        };
    }

    private static void AddDetailRow(Grid grid, int row, string label, TextBox value)
    {
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 2, 12, 2) };
        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, 0);
        grid.Children.Add(caption);

        value.Margin = new Thickness(0, 2, 0, 2);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
    }

    private IngestHistoryRow? Selected => _grid.SelectedItem as IngestHistoryRow;

    private void RefreshDetail()
    {
        var row = Selected;
        _outputPathBox.Text = row?.OutputPath ?? string.Empty;
        _sourcePathBox.Text = row?.SourcePath ?? string.Empty;
    }

    // 旧スキーマからの移送行などでパスが空のことがある。空のまま explorer を叩かず、項目を伏せる。
    private void RefreshMenuState()
    {
        var row = Selected;
        var hasOutput = !string.IsNullOrWhiteSpace(row?.OutputPath);
        var hasSource = !string.IsNullOrWhiteSpace(row?.SourcePath);

        _openOutput.IsEnabled = hasOutput;
        _copyOutput.IsEnabled = hasOutput;
        _openSource.IsEnabled = hasSource;
        _copySource.IsEnabled = hasSource;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Reveal(output: true);
        e.Handled = true;
    }

    private static void OnRowRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // コンテキストメニューは「選択行」に対して働くので、開く前にクリックされた行を選択状態にする
        // (右クリックが選択を動かすかは DataGrid の既定に依存するため、ここで確定させる)。
        // e.Handled は立てない。メニュー自体は通常どおり開く必要がある。
        if (sender is DataGridRow row)
            row.IsSelected = true;
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        Reveal(output: true);
        e.Handled = true; // DataGrid 既定の「次の行へ移動」を抑える
    }

    private async void Reveal(bool output)
    {
        var row = Selected;
        if (row is null)
            return;

        var path = output ? row.OutputPath : row.SourcePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _action.Text = output ? "出力先が記録されていません" : "取込元が記録されていません";
            return;
        }

        try
        {
            // 不達 NAS の存在確認は数秒かかりうる。await 中も窓は操作可能なので、進行中であることを示す。
            _action.Text = "エクスプローラーで開いています…";
            var result = await ExplorerLauncher.RevealAsync(path);
            _action.Text = result.Message;
        }
        catch (Exception ex)
        {
            // async void。ここで漏らすとディスパッチャの未処理例外になり、トレイごと巻き添えに落ちる。
            TrayLog.Warn($"エクスプローラー起動でエラーが発生しました: {ex}");
            _action.Text = "エクスプローラーを開けませんでした(gui.log 参照)";
        }
    }

    private void CopyPath(bool output)
    {
        var row = Selected;
        var path = output ? row?.OutputPath : row?.SourcePath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            Clipboard.SetText(path);
            _action.Text = "パスをコピーしました";
        }
        catch (Exception ex)
        {
            // クリップボードは他プロセスにロックされることがある(CLIPBRD_E_CANT_OPEN)。
            TrayLog.Warn($"クリップボードへのコピーに失敗しました: {ex}");
            _action.Text = "パスをコピーできませんでした";
        }
    }

    private void Reload()
    {
        try
        {
            var rows = _load();
            _grid.ItemsSource = rows;
            _count.Text = $"{rows.Count} 件";
            _action.Text = string.Empty;
            RefreshDetail();
        }
        catch (Exception ex)
        {
            _count.Text = "読み込みに失敗しました";
            MessageBox.Show(
                this,
                $"取込履歴の読み込みに失敗しました:\n{ex.Message}",
                "取込履歴", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
