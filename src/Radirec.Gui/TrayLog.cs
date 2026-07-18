using System.IO;
using System.Runtime.Versioning;

namespace Radirec.Gui;

/// <summary>
/// トレイ受け手の簡易ログ。WinExe はコンソールを持たないため、診断を
/// <c>%LocalAppData%\Radirec\gui.log</c> へ追記する(契約ドリフト・parse 失敗の観測点。ADR-0009)。
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
