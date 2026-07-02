using Radirec.Core.Configuration;

namespace Radirec.Core.Normalization;

/// <summary>番組の生名 → 正規番組名の解決結果。</summary>
public sealed record ProgramResolution(string Canonical, bool UsedFallback);

public interface IProgramResolver
{
    ProgramResolution Resolve(string canonicalStation, string rawProgram);
}

/// <summary>
/// 番組 alias で (正規局, 生番組名) を正規番組名に寄せる。未登録は生名にフォールバック。
/// </summary>
public sealed class ProgramResolver : IProgramResolver
{
    private readonly Dictionary<(string Station, string Program), string> _map = new();

    public ProgramResolver(ProgramMasterConfig config)
    {
        foreach (var entry in config.Programs)
        {
            var stationKey = TextNormalizer.ToMatchKey(entry.Match.Station);
            foreach (var programKey in entry.Match.Keys)
            {
                var key = (stationKey, TextNormalizer.ToMatchKey(programKey));
                _map[key] = entry.Canonical.Album;
            }
        }
    }

    public ProgramResolution Resolve(string canonicalStation, string rawProgram)
    {
        var key = (TextNormalizer.ToMatchKey(canonicalStation), TextNormalizer.ToMatchKey(rawProgram));
        return _map.TryGetValue(key, out var canonical)
            ? new ProgramResolution(canonical, UsedFallback: false)
            : new ProgramResolution(rawProgram.Trim(), UsedFallback: true);
    }
}
