using System.IO;
using System.Runtime.Versioning;
using Airshelf.Gui.Core.Management;
using Airshelf.Hosting;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Airshelf.Gui;

/// <summary>設定ページ(#152)がシェルに頼むこと(開く・読む・確認・再起動)の Windows 側の実装。</summary>
[SupportedOSPlatform("windows")]
internal sealed class SettingsPageActions : ISettingsPageActions
{
    public async Task<SettingsFileOpenResult> OpenAsync(ConfigFile file, string path)
    {
        var result = await ConfigFileOpener.OpenAsync(file, path);
        return new SettingsFileOpenResult(result.Ok, result.Message);
    }

    public string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    // W-309。取り込みを一時停止して作り直すので、既定はキャンセル側(トレイの「設定リロード…」と同じ)。
    public bool ConfirmReload(string message) =>
        MessageBox.Show(message, "設定リロード", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            == MessageBoxResult.OK;

    // W-404。トレイの「再読み込みして再起動」と同じ(プロセスごと起動し直して、直した設定を読み直す)。
    public void Restart() => System.Windows.Forms.Application.Restart();
}
