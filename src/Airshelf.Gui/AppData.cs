using System.IO;
using System.Runtime.Versioning;
using Airshelf.Hosting;

namespace Airshelf.Gui;

/// <summary>
/// 常駐トレイのローカル状態(ログ等)を置くディレクトリ。**取込データ(台帳・設定・マスタ)と同じ
/// <see cref="ConfigPaths.DataRoot"/>(<c>%LOCALAPPDATA%\RadirecData</c>)に揃える** — Velopack の
/// インストール先(<c>%LOCALAPPDATA%\Radirec</c>)とは別で、アンインストールで消えない(ADR-0013 / S5)。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AppData
{
    /// <summary>ローカル状態ディレクトリ(未作成のこともある。書き手が CreateDirectory する)。</summary>
    public static string Dir { get; } = ConfigPaths.DataRoot;

    /// <summary><see cref="Dir"/> 配下のファイルパスを組み立てる。</summary>
    public static string File(string name) => Path.Combine(Dir, name);
}
