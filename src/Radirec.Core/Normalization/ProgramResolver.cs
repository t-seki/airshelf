using Radirec.Core.Configuration;

namespace Radirec.Core.Normalization;

/// <summary>番組の生名 → 正規番組名の解決結果。</summary>
public sealed record ProgramResolution(string Canonical, bool UsedFallback);

public interface IProgramResolver
{
    /// <param name="programFolder">番組フォルダ名(番組の同一性の拠り所。ADR-0001)。</param>
    ProgramResolution Resolve(string canonicalStation, string programFolder);
}

/// <summary>
/// 番組 alias で (正規局, 番組フォルダ名) を正規番組名(= アルバム名)に寄せる。
/// 未登録はフォルダ名にフォールバック。複数フォルダ → 1 アルバムの統合もここで行う。
/// </summary>
public sealed class ProgramResolver : IProgramResolver
{
    private readonly Dictionary<(string Station, string Folder), string> _map = new();

    public ProgramResolver(ProgramMasterConfig config)
    {
        foreach (var entry in config.Programs)
        {
            var stationKey = TextNormalizer.ToMatchKey(entry.Match.Station);
            foreach (var folder in entry.Match.Folders)
            {
                var key = (stationKey, TextNormalizer.ToMatchKey(folder));
                _map[key] = entry.Canonical.Album;
            }
        }
    }

    public ProgramResolution Resolve(string canonicalStation, string programFolder)
    {
        var key = (TextNormalizer.ToMatchKey(canonicalStation), TextNormalizer.ToMatchKey(programFolder));
        return _map.TryGetValue(key, out var canonical)
            ? new ProgramResolution(canonical, UsedFallback: false)
            : new ProgramResolution(programFolder.Trim(), UsedFallback: true);
    }
}
