using Radirec.Hosting;

namespace Radirec.Cli.Core;

/// <summary>コマンドが設定(radirec.json)を読む口。テストでは読めない設定などに差し替える。</summary>
public interface ICliConfigSource
{
    /// <summary>設定を読み、相対パスを config ディレクトリ基準へ rebase する(ADR-0006)。読めなければ理由を返す。</summary>
    bool TryLoad(out RadirecConfig? config, out string? error);
}

/// <summary>
/// 既定の設定ファイル(<see cref="ConfigPaths.DefaultConfigPath"/>。トレイと同じ)を読む。実体は
/// <see cref="Radirec.Hosting.ConfigLoading"/>。別の場所の設定を指すオプションは無い(ADR-0020)。
/// </summary>
public sealed class DefaultCliConfigSource : ICliConfigSource
{
    public bool TryLoad(out RadirecConfig? config, out string? error)
    {
        var ok = ConfigLoading.TryLoad(ConfigPaths.DefaultConfigPath, out var loaded, out error);
        config = loaded;
        return ok;
    }
}
