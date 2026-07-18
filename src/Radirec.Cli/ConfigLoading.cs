using Radirec.Hosting;
using Spectre.Console;

namespace Radirec.Cli;

/// <summary>
/// 設定の発見・読み込みの CLI 向けラッパ。実体の読み込み/rebase は
/// <see cref="Radirec.Hosting.ConfigLoading"/> に委譲し、ここでは失敗理由のコンソール表示だけを担う。
/// </summary>
internal static class ConfigLoading
{
    /// <summary>
    /// 設定パスを解決(省略時 <see cref="ConfigPaths.DefaultConfigPath"/>)し、読み込み・相対パスの
    /// config ディレクトリ基準への rebase(ADR-0006)まで行う。失敗時は理由を表示して <c>null</c> を返す
    /// (呼び出し側は exit 1)。
    /// </summary>
    public static RadirecConfig? Load(string? explicitPath)
    {
        if (Radirec.Hosting.ConfigLoading.TryLoad(ConfigPaths.Resolve(explicitPath), out var config, out var error))
            return config;

        AnsiConsole.MarkupLineInterpolated($"[red]{error}[/]");
        return null;
    }
}
