using Airshelf.Gui.Core.Management;
using Airshelf.Hosting;

namespace Airshelf.Gui;

/// <summary>設定ページ(#152)に渡す、起動時に決まるもの。</summary>
/// <param name="ConfigPath">airshelf.json の絶対パス(読めなかったときも。直すために開く)。</param>
/// <param name="StartupConfig">起動時に読めた設定。読めなかったら null(縮退パネルを出す)。</param>
/// <param name="ConfigError">airshelf.json を読めなかった理由(W-402)。読めたら null。</param>
/// <param name="ToastSetting">新規取込のトーストの設定(W-302)。</param>
/// <param name="StartError">
/// 設定は読めたが取り込みワーカーを起動できなかった(W-104)ときの例外のメッセージ(W-407。#168)。起動できたなら null。
/// </param>
internal sealed record SettingsPageContext(
    string? ConfigPath, AirshelfConfig? StartupConfig, string? ConfigError, IngestToastSetting ToastSetting,
    string? StartError = null);
