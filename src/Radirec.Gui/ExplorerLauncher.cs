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

        var exists = await ProbeAsync(() => File.Exists(path) || Directory.Exists(path));
        // 判定不能(タイムアウト / 例外)は「ある」側に倒し、explorer に判断を委ねる。
        if (!exists.Determined || exists.Value)
            return Start($"/select,{Quote(path)}", $"エクスプローラーで開きました: {PathReveal.FileName(path)}");

        var ancestor = await ProbeAsync(() => PathReveal.NearestExistingDirectory(path, Directory.Exists));

        // 祖先探索も判定不能でありうる(遅い共有への Directory.Exists を何度も呼ぶため、
        // 単発の File.Exists より時間切れしやすい)。ここで「見つかりません」と断じると、
        // 遅いだけの共有を存在しないと決めつけることになるので、存在確認と同じく explorer へ委ねる。
        if (!ancestor.Determined)
            return Start($"/select,{Quote(path)}", "確認に時間がかかったため、そのまま開きます");

        if (ancestor.Value is null)
            return new RevealResult(false, $"見つかりません: {path}");

        return Start(Quote(ancestor.Value), $"ファイルが見つからないため親フォルダを開きました: {ancestor.Value}");
    }

    /// <summary>
    /// ファイルシステムへの問い合わせを、UI スレッドを塞がずタイムアウト付きで行う。
    /// タイムアウト / 例外は <c>Determined = false</c>(判定不能)として返す。**結果の null / false と
    /// 判定不能を同じ値に潰さない**のが要点で、呼び出し側はこの 2 つを別々に扱う必要がある
    /// (「祖先が無い」と「遅くて確かめられなかった」では取るべき縮退が違う)。
    /// タイムアウトしてもワーカースレッドは SMB の応答を待ち続けるが、UI からは切り離されている。
    /// </summary>
    private static async Task<(bool Determined, T? Value)> ProbeAsync<T>(Func<T> probe)
    {
        try
        {
            var work = Task.Run(probe);
            // ConfigureAwait は付けない(WPF の Dispatcher に戻ってから呼び出し元が UI を触るため)。
            var finished = await Task.WhenAny(work, Task.Delay(ProbeTimeout));
            return finished == work ? (true, await work) : (false, default);
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"パスの存在確認に失敗しました: {ex}");
            return (false, default);
        }
    }

    // 引用は末尾バックスラッシュの扱いを誤りやすく、Windows でしか再現しないので、テスト可能な
    // Gui.Core 側(PathReveal)に置いてある。
    private static string Quote(string path) => PathReveal.QuoteForCommandLine(path);

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
