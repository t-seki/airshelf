using System.Text.Json;
using Radirec.Hosting;
using Spectre.Console;

namespace Radirec.Cli;

/// <summary>
/// 設定の発見・読み込み・相対パス解決を 1 箇所に集約する(ingest/refresh/service で共有)。
/// これにより「相対パスの rebase を忘れる」「不正 JSON を握らず raw クラッシュする」といった
/// コマンド間のドリフトを構造的に防ぐ。
/// </summary>
internal static class ConfigLoading
{
    /// <summary>
    /// 設定パスを解決(省略時 <see cref="ConfigPaths.DefaultConfigPath"/>)し、存在確認・読み込み・
    /// 相対パスの config ディレクトリ基準への rebase(ADR-0006)まで行う。
    /// 失敗時は理由を表示して <c>null</c> を返す(呼び出し側は exit 1)。
    /// </summary>
    public static RadirecConfig? Load(string? explicitPath)
    {
        var configPath = ConfigPaths.Resolve(explicitPath);
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]設定ファイルが見つかりません:[/] {configPath}");
            return null;
        }

        try
        {
            var config = ConfigLoader.LoadRequired<RadirecConfig>(configPath);
            ConfigPaths.RebaseAgainst(config, configPath);
            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]設定の読み込みに失敗しました:[/] {ex.Message}");
            return null;
        }
    }
}
