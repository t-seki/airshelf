using Airshelf.Hosting;

namespace Airshelf.Cli.Core;

/// <summary>コマンドが設定(airshelf.json)を読む口。テストでは読めない設定などに差し替える。</summary>
public interface ICliConfigSource
{
    /// <summary>設定を読み、相対パスを config ディレクトリ基準へ rebase する(ADR-0006)。読めなければ理由を返す。</summary>
    bool TryLoad(out AirshelfConfig? config, out string? error);
}

/// <summary>
/// 既定の設定ファイル(<see cref="ConfigPaths.DefaultConfigPath"/>。トレイと同じ)を読む。実体は
/// <see cref="Airshelf.Hosting.ConfigLoading"/>。別の場所の設定を指すオプションは無い(ADR-0020)。
/// </summary>
public sealed class DefaultCliConfigSource : ICliConfigSource
{
    public bool TryLoad(out AirshelfConfig? config, out string? error)
    {
        var ok = ConfigLoading.TryLoad(ConfigPaths.DefaultConfigPath, out var loaded, out error);
        config = loaded;
        return ok;
    }
}
