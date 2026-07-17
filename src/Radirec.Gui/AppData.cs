using System.IO;
using System.Runtime.Versioning;

namespace Radirec.Gui;

/// <summary>
/// 受け手トレイのローカル状態を置く <c>%LocalAppData%\Radirec</c> の単一の権威。ログ(<see cref="TrayLog"/>)・
/// 購読チェックポイント(<see cref="CheckpointStore"/>)が同じ基点を共有し、配置場所が分岐しないようにする。
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
