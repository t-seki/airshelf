using System.IO;
using System.Runtime.Versioning;

namespace Radirec.Gui;

/// <summary>
/// 常駐トレイのローカル状態を置く <c>%LocalAppData%\Radirec</c> の単一の権威。ログ(<see cref="TrayLog"/>)が
/// この基点を使う。取込データ(台帳・設定)は別で、<c>%ProgramData%\radirec</c> にある(ADR-0006。
/// per-user 化は S5 / #67)。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AppData
{
    /// <summary>受け手のローカル状態ディレクトリ(未作成のこともある。書き手が CreateDirectory する)。</summary>
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Radirec");

    /// <summary><see cref="Dir"/> 配下のファイルパスを組み立てる。</summary>
    public static string File(string name) => Path.Combine(Dir, name);
}
