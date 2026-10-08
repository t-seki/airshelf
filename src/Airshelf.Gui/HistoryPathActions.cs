using System.Runtime.Versioning;
using Airshelf.Gui.Core.Management;

namespace Airshelf.Gui;

/// <summary>取込履歴の行から出力先・取込元をエクスプローラーで開く / パスをコピーする(#87。管理画面 #151)。</summary>
[SupportedOSPlatform("windows")]
internal sealed class HistoryPathActions : IHistoryPathActions
{
    public async Task<string> RevealAsync(string path) => (await ExplorerLauncher.RevealAsync(path)).Message;

    public bool TryCopy(string path)
    {
        try
        {
            System.Windows.Clipboard.SetText(path);
            return true;
        }
        catch (Exception ex)
        {
            // クリップボードは他のプロセスが掴んでいることがある(CLIPBRD_E_CANT_OPEN)。
            TrayLog.Warn($"クリップボードへのコピーに失敗しました: {ex}");
            return false;
        }
    }
}
