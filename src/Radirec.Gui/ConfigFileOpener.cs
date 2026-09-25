using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Radirec.Hosting;

namespace Radirec.Gui;

/// <summary>設定ファイルを開いた結果。<see cref="Message"/> はバルーンに出す文言(知らせることが無ければ null)。</summary>
/// <param name="Ok">エディタを起動できたか(利用者が「このファイルを開く方法」を閉じたときも true。失敗ではない)。</param>
internal sealed record OpenConfigResult(bool Ok, string? Message);

/// <summary>
/// 設定ページの「ファイルを開く」(#143 / #152)。設定ファイルを Windows で <c>.json</c> に関連付けたアプリで開く(シェル実行)。
/// ファイルが無ければ、起動時と同じ見本から作ってから開く(<see cref="ConfigSeeder.SeedFileIfAbsent"/>)。
/// 保存しても自動では反映しない(利用者が管理画面から設定リロードか、番組の設定の再適用を行う。requirements §8・§10.3)。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConfigFileOpener
{
    private const int ErrorNoAssociation = 1155; // ERROR_NO_ASSOCIATION: 拡張子に関連付けが無い
    private const int ErrorCancelled = 1223;     // ERROR_CANCELLED: 利用者が「このファイルを開く方法」を閉じた

    /// <summary>
    /// <paramref name="path"/> を開く。存在確認と見本からの生成はワーカースレッドで行う(<c>ProgramsFile</c> が
    /// NAS を指していても UI スレッドを塞がない)。シェル実行は呼び出し元(UI)スレッドで行う。
    /// </summary>
    public static async Task<OpenConfigResult> OpenAsync(ConfigFile file, string path)
    {
        var name = Path.GetFileName(path);
        bool existed;
        bool ready;
        try
        {
            (existed, ready) = await Task.Run(() =>
            {
                var exists = File.Exists(path);
                return (exists, exists || ConfigSeeder.SeedFileIfAbsent(file, path));
            });
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"{name} の存在確認に失敗しました({path}): {ex}");
            return new OpenConfigResult(false, $"{name} を開けませんでした(gui.log 参照)");
        }

        if (!ready)
            return new OpenConfigResult(false, $"{name} が無く、見本から作れませんでした(gui.log 参照)");

        if (!Start(path, name))
            return new OpenConfigResult(false, $"{name} を開けませんでした(gui.log 参照)");

        return new OpenConfigResult(true, existed ? null : $"{name} が無かったので見本から作りました");
    }

    private static bool Start(string path, string name)
    {
        try
        {
            try
            {
                // 関連付けたアプリで開く。既に起動しているエディタがファイルを引き取ると null が返る(失敗ではない)。
                using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoAssociation)
            {
                // .json に関連付けが無い。Windows の「このファイルを開く方法」に任せる。
                TrayLog.Info($"{name} に関連付けたアプリが無いため、開く方法を選んでもらいます");
                using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "openas" });
            }
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return true; // 利用者が「このファイルを開く方法」を閉じた。失敗として知らせない
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"{name} を開けませんでした({path}): {ex}");
            return false;
        }
    }
}
