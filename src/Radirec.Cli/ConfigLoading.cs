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
        if (TryLoad(ConfigPaths.Resolve(explicitPath), out var config, out var error))
            return config;

        AnsiConsole.MarkupLineInterpolated($"[red]{error}[/]");
        return null;
    }

    /// <summary>
    /// <paramref name="configPath"/>(解決済み絶対パス)から読み込み・rebase する。コンソールに書かず
    /// 失敗理由を <paramref name="error"/> で返す。設定リロード(制御チャネル。ADR-0010)のように
    /// 対話コンソールが無い経路で使う。
    /// </summary>
    public static bool TryLoad(string configPath, out RadirecConfig? config, out string? error)
    {
        config = null;
        if (!File.Exists(configPath))
        {
            error = $"設定ファイルが見つかりません: {configPath}";
            return false;
        }

        try
        {
            var loaded = ConfigLoader.LoadRequired<RadirecConfig>(configPath);
            ConfigPaths.RebaseAgainst(loaded, configPath);
            config = loaded;
            error = null;
            return true;
        }
        // JsonException / IOException に限らず、UnauthorizedAccessException(権限)・
        // InvalidOperationException(内容が null 等)含むあらゆる読込失敗を「クリーンな失敗」に正規化する。
        // reload はこの戻り値を listener スレッドで ReloadResult.Failed に変換するため、ここで throw を
        // 漏らすと制御チャネルが応答を返せず誤って「サービス応答なし」になる(ADR-0010)。
        catch (Exception ex)
        {
            error = $"設定の読み込みに失敗しました: {ex.Message}";
            return false;
        }
    }
}
