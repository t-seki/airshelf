using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Radirec.Gui.Core;

namespace Radirec.Gui;

/// <summary>エクスプローラー起動の結果。<see cref="Message"/> は呼び出し元のステータス行に出す文言。</summary>
/// <param name="Ok">起動できたか(ファイル本体に届いたかは問わない。親フォルダへの縮退も true)。</param>
internal sealed record RevealResult(bool Ok, string Message);

/// <summary>
/// 取込履歴からファイルをエクスプローラーで開く(#87)。<c>/select</c> で親フォルダを開いた上で
/// そのファイルを選択状態にする。ファイルが無いときは最近接の存在する祖先フォルダへ縮退する。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ExplorerLauncher
{
    /// <summary>
    /// 存在確認の待ち上限。不達 NAS への <see cref="File.Exists"/> は SMB のタイムアウトで数秒ブロック
    /// しうるため上限を置く。超えたら「存在するかもしれない」側に倒して <c>/select</c> をそのまま投げる
    /// (遅いだけの共有を「見つからない」と断じない)。
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <paramref name="path"/> をエクスプローラーで開く。存在確認はワーカースレッドで行うため、
    /// UI スレッドから await しても窓は固まらない。
    /// </summary>
    public static async Task<RevealResult> RevealAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new RevealResult(false, "パスが記録されていません");

        // null(判定不能 = タイムアウト/例外)は「ある」側に倒す。explorer に投げて判断を委ねる。
        // ※ T を bool? で明示するのは、判定不能を false(= 見つからない)と潰さないため。
        if (await ProbeAsync<bool?>(() => File.Exists(path) || Directory.Exists(path)) != false)
            return Start($"/select,\"{path}\"", $"エクスプローラーで開きました: {PathReveal.FileName(path)}");

        var ancestor = await ProbeAsync(() => PathReveal.NearestExistingDirectory(path, Directory.Exists));
        if (ancestor is null)
            return new RevealResult(false, $"見つかりません: {path}");

        return Start($"\"{ancestor}\"", $"ファイルが見つからないため親フォルダを開きました: {ancestor}");
    }

    /// <summary>
    /// ファイルシステムへの問い合わせを、UI スレッドを塞がずタイムアウト付きで行う。
    /// タイムアウト / 例外は「判定不能」として default(参照型は null、bool? は null)を返す。
    /// タイムアウトしてもワーカースレッドは SMB の応答を待ち続けるが、UI からは切り離されている。
    /// </summary>
    private static async Task<T?> ProbeAsync<T>(Func<T> probe)
    {
        try
        {
            var work = Task.Run(probe);
            // ConfigureAwait は付けない(WPF の Dispatcher に戻ってから呼び出し元が UI を触るため)。
            var finished = await Task.WhenAny(work, Task.Delay(ProbeTimeout));
            return finished == work ? await work : default;
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"パスの存在確認に失敗しました: {ex}");
            return default;
        }
    }

    private static RevealResult Start(string arguments, string okMessage)
    {
        try
        {
            // 引数は必ず生の文字列で渡す。ArgumentList は各要素を 1 トークンとして引用符化するため
            // "/select,C:\a\b.m4a" となり explorer が解釈できず、黙ってドキュメントフォルダを開く。
            // カンマの後にスペースを入れてもいけない。
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", arguments)
            {
                UseShellExecute = false,
            });

            // explorer.exe は成功時も終了コード 1 を返すため、ExitCode で成否を判定してはならない。
            // 失敗は Process.Start が投げる Win32Exception としてのみ観測する。
            return new RevealResult(true, okMessage);
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"エクスプローラーの起動に失敗しました({arguments}): {ex}");
            return new RevealResult(false, "エクスプローラーを開けませんでした(gui.log 参照)");
        }
    }
}
