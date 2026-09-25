using Radirec.Gui.Core.Management;
using Radirec.Hosting;

namespace Radirec.Gui;

/// <summary>設定ページ(#152)に渡す、起動時に決まるもの。</summary>
/// <param name="ConfigPath">radirec.json の絶対パス(読めなかったときも。直すために開く)。</param>
/// <param name="StartupConfig">起動時に読めた設定。読めなかったら null(縮退パネルを出す)。</param>
/// <param name="ConfigError">radirec.json を読めなかった理由(W-402)。読めたら null。</param>
/// <param name="ToastSetting">新規取込のトーストの設定(W-302)。</param>
internal sealed record SettingsPageContext(
    string? ConfigPath, RadirecConfig? StartupConfig, string? ConfigError, IngestToastSetting ToastSetting);
