using System.Runtime.Versioning;

namespace Airshelf.Gui.Pages;

/// <summary>管理画面の設定ページ(#152)。中身は <c>SettingsPageViewModel</c> に結ぶだけ。</summary>
[SupportedOSPlatform("windows")]
public partial class SettingsPage : System.Windows.Controls.UserControl // WinForms の UserControl と名前が衝突するので完全修飾
{
    public SettingsPage() => InitializeComponent();
}
