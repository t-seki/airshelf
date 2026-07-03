using Radirec.Core.Configuration;

namespace Radirec.Core.Normalization;

/// <summary>番組の生名 → 正規番組名の解決結果。</summary>
/// <param name="PartIndex">
/// 複数フォルダを統合した番組で、このフォルダが何番目のパートか(1 始まり)。統合でない場合 null。
/// 出力ファイル名の <c>-N</c> サフィックスに使う。
/// </param>
public sealed record ProgramResolution(string Canonical, bool UsedFallback, int? PartIndex = null);

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
    private readonly Dictionary<(string Station, string Folder), (string Album, int? PartIndex)> _map = new();

    public ProgramResolver(ProgramMasterConfig config)
    {
        foreach (var entry in config.Programs)
        {
            var stationKey = TextNormalizer.ToMatchKey(entry.Match.Station);

            // 正規化キーで重複排除(表記ゆれの同一フォルダを 1 つに畳む)。パート番号は
            // **異なる**フォルダが 2 つ以上ある場合のみ、その並び順で 1 始まりに振る。
            var folderKeys = entry.Match.Folders
                .Select(TextNormalizer.ToMatchKey)
                .Where(k => !string.IsNullOrEmpty(k))
                .Distinct()
                .ToList();
            var multiPart = folderKeys.Count > 1;

            for (var i = 0; i < folderKeys.Count; i++)
            {
                var partIndex = multiPart ? i + 1 : (int?)null;
                _map[(stationKey, folderKeys[i])] = (entry.Canonical.Album, partIndex);
            }
        }
    }

    public ProgramResolution Resolve(string canonicalStation, string programFolder)
    {
        var key = (TextNormalizer.ToMatchKey(canonicalStation), TextNormalizer.ToMatchKey(programFolder));
        return _map.TryGetValue(key, out var hit)
            ? new ProgramResolution(hit.Album, UsedFallback: false, PartIndex: hit.PartIndex)
            : new ProgramResolution(programFolder.Trim(), UsedFallback: true);
    }
}
