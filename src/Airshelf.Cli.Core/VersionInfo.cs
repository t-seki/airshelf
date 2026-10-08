using System.Reflection;

namespace Airshelf.Cli.Core;

/// <summary>アセンブリに刻まれた版を返す(タグ駆動・CI 注入。ADR-0005 / #16)。</summary>
public static class VersionInfo
{
    /// <summary>
    /// <c>AssemblyInformationalVersion</c> から表示用の版を得る。exe(airshelf)のアセンブリを渡す。
    /// .NET は末尾に <c>+&lt;commit hash&gt;</c> のビルドメタデータを付けうるため、最初の <c>+</c> で切る。
    /// </summary>
    public static string Of(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0-dev";
        var plus = info.IndexOf('+');
        if (plus >= 0)
            info = info[..plus];
        // 版部分が空(例: "+abc123" のようにセマンティック版が無い)なら実用値へフォールバック。
        return string.IsNullOrEmpty(info) ? "0.0.0-dev" : info;
    }
}
