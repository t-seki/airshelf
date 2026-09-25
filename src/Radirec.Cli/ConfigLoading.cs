using Radirec.Hosting;
using Spectre.Console;

namespace Radirec.Cli;

/// <summary>
/// 設定の読み込みの CLI 向けラッパ。実体の読み込み/rebase は
/// <see cref="Radirec.Hosting.ConfigLoading"/> に委譲し、ここでは失敗理由のコンソール表示だけを担う。
/// </summary>
internal static class ConfigLoading
{
    /// <summary>
    /// 既定の設定ファイル(<see cref="ConfigPaths.DefaultConfigPath"/>。トレイと同じ)を読み込み、相対パスを
    /// config ディレクトリ基準へ rebase する(ADR-0006)。別の場所の設定を指すオプションは無い(ADR-0020)。
    /// 失敗時は理由を表示して <c>null</c> を返す(呼び出し側は exit 1)。
    /// </summary>
    public static RadirecConfig? Load()
    {
        if (Radirec.Hosting.ConfigLoading.TryLoad(ConfigPaths.DefaultConfigPath, out var config, out var error))
            return config;

        AnsiConsole.MarkupLineInterpolated($"[red]{error}[/]");
        return null;
    }
}
