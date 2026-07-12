using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Radirec.Gui;

/// <summary>
/// ログオン自動起動を HKCU\...\Run へ自己登録する(ADR-0009)。admin 不要・タスクマネージャの
/// スタートアップから可視/ON-OFF 可能。登録ロジックをアプリ内に閉じる。
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistrar
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RadirecTray";

    /// <summary>現在の実行ファイルを自動起動に登録する(冪等)。</summary>
    public static void Register(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        var quoted = $"\"{exePath}\"";
        if ((key.GetValue(ValueName) as string) != quoted)
            key.SetValue(ValueName, quoted, RegistryValueKind.String);
    }

    public static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
