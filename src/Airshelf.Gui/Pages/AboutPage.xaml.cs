using System.Runtime.Versioning;
// WinForms も併用するプロジェクトなので、UserControl は WPF の方を明示する。
using UserControl = System.Windows.Controls.UserControl;

namespace Airshelf.Gui.Pages;

/// <summary>
/// 管理画面のバージョン情報ページ(#157)。中身は <see cref="Airshelf.Gui.Core.Management.AboutPageViewModel"/> に結ぶだけ。
/// 適用の確認(W-508)のダイアログは <see cref="ManagementWindowHost"/> が出す。
/// </summary>
[SupportedOSPlatform("windows")]
public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
    }
}
