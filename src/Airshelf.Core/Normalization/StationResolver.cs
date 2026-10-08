using Airshelf.Core.Configuration;

namespace Airshelf.Core.Normalization;

/// <summary>放送局の生名 → 正規表示名の解決結果。</summary>
public sealed record StationResolution(string Canonical, bool UsedFallback);

public interface IStationResolver
{
    StationResolution Resolve(string rawStation);
}

/// <summary>
/// 放送局マスタ(alias)で生の局名を正規名に寄せる。未登録は生名にフォールバック(UsedFallback=true)。
/// </summary>
public sealed class StationResolver : IStationResolver
{
    private readonly Dictionary<string, string> _byMatchKey;

    public StationResolver(StationMasterConfig config)
    {
        _byMatchKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var station in config.Stations)
        {
            Register(station.Canonical, station.Canonical);
            foreach (var alias in station.Aliases)
                Register(alias, station.Canonical);
        }
    }

    private void Register(string raw, string canonical)
    {
        var key = TextNormalizer.ToMatchKey(raw);
        if (!string.IsNullOrEmpty(key))
            _byMatchKey[key] = canonical;
    }

    public StationResolution Resolve(string rawStation)
    {
        var key = TextNormalizer.ToMatchKey(rawStation);
        return _byMatchKey.TryGetValue(key, out var canonical)
            ? new StationResolution(canonical, UsedFallback: false)
            : new StationResolution(rawStation.Trim(), UsedFallback: true);
    }
}
