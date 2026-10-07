using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Airshelf.Gui;

/// <summary>
/// DataGrid で起きたマウスの操作が、どの行の上で起きたかを探す(#171)。
/// <para>
/// 行のイベントは、以前は <c>DataGrid.RowStyle</c> の <c>EventSetter</c> で拾っていた。だが <c>RowStyle</c> を置くと、
/// Fluent テーマの DataGrid が配る行のスタイル(<c>DefaultDataGridRowStyle</c>)を上書きしてしまい、ダークでも行だけ
/// クラシックの見た目になる。なので <c>RowStyle</c> は置かず、DataGrid のイベントで受けて、ここで行を探す。
/// 見出し・スクロールバーなど行の外で起きたときは null(DataGrid の <c>MouseDoubleClick</c> は見出しでも起きる)。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DataGridRowHit
{
    public static DataGridRow? RowOf(object? originalSource)
    {
        var current = originalSource as DependencyObject;
        while (current is not null)
        {
            if (current is DataGridRow row)
                return row;

            // セルの中の Run などは Visual ではないので、論理ツリーで親を辿る(VisualTreeHelper は Visual 以外で例外を投げる)。
            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
