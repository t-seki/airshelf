using System.IO;
using System.Runtime.Versioning;

namespace Radirec.Gui;

/// <summary>
/// 常駐トレイの簡易ログ。WinExe はコンソールを持たないため、診断を
/// <c>%LOCALAPPDATA%\RadirecData\gui.log</c> へ追記する(<see cref="AppData"/> 経由。ADR-0009 / 0013)。
/// **Velopack のインストール先 <c>%LOCALAPPDATA%\Radirec</c> ではない** — そちらは更新/アンインストールで消える。
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayLog
{
    private static readonly string LogPath = AppData.File("gui.log");

    private static readonly Lock Gate = new();

    public static void Error(string message) => Write("ERROR", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Info(string message) => Write("INFO", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ログ書込失敗で常駐を止めない。
        }
    }
}
