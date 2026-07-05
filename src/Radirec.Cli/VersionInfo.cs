using System.Reflection;

namespace Radirec.Cli;

/// <summary>アセンブリに刻まれた版を返す(タグ駆動・CI 注入。ADR-0005 / #16)。</summary>
internal static class VersionInfo
{
    /// <summary>
    /// <c>AssemblyInformationalVersion</c> から表示用の版を得る。
    /// .NET は末尾に <c>+&lt;commit hash&gt;</c> のビルドメタデータを付けうるため、最初の <c>+</c> で切る。
    /// </summary>
    public static string Current
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0-dev";
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }
}
