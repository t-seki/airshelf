using System.Reflection;

namespace Radirec.Gui.Core;

/// <summary>
/// トレイに表示する版文字列を、アセンブリの informational version から整形する(S1 / #63)。
/// 版は CI がタグから <c>-p:Version</c> で注入し、ローカル非 CI ビルドは
/// <c>Directory.Build.props</c> のフォールバック <c>0.0.0-dev</c> になる。
/// 表示では SourceLink 等が付ける build metadata(SemVer の <c>+&lt;hash&gt;</c> 以降)を落とし、
/// prerelease サフィックス(<c>-rc1</c> 等)は残す。純粋関数なので単体テスト可能。
/// </summary>
public static class AppVersion
{
    private const string Unknown = "unknown";

    /// <summary>指定アセンブリの informational version を表示用に整形して返す。</summary>
    public static string Of(Assembly assembly) =>
        Format(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// informational version 文字列を表示用に整形する。build metadata(<c>+</c> 以降)を除去し、
    /// prerelease は保持する。null/空白は <c>unknown</c> にフォールバック。
    /// </summary>
    public static string Format(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return Unknown;

        var version = informationalVersion.Trim();

        // build metadata(SemVer の '+' 以降。SourceLink の +<commit> 等)は表示に含めない。
        // 切り出した後にもう一度 Trim する('+' 直前の空白が末尾に残るのを防ぐ)。
        var plus = version.IndexOf('+');
        if (plus >= 0)
            version = version[..plus].Trim();

        return version.Length == 0 ? Unknown : version;
    }
}
