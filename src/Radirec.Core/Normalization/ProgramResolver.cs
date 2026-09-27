using Radirec.Core.Configuration;
using Radirec.Core.Model;

namespace Radirec.Core.Normalization;

/// <summary>番組の生名 → 正規番組名の解決結果。</summary>
/// <param name="PartIndex">
/// 複数フォルダを統合した番組で、このフォルダが何番目のパートか(1 始まり)。統合でない場合 null。
/// 出力ファイル名の <c>-N</c> サフィックスに使う。
/// </param>
public sealed record ProgramResolution(string Canonical, bool UsedFallback, int? PartIndex = null);

/// <summary>ファイル名の番組名から見た扱い(ADR-0021)。</summary>
/// <param name="Album"><see cref="FilenameProgramVerdict.OtherProgram"/> のときの判断のアルバム名。ほかは null。</param>
public sealed record FilenameProgramJudgement(FilenameProgramVerdict Verdict, string? Album = null)
{
    public static FilenameProgramJudgement ContainsFolder { get; } = new(FilenameProgramVerdict.ContainsFolder);
}

public interface IProgramResolver
{
    /// <param name="programFolder">番組フォルダ名(番組の同一性の拠り所。ADR-0001)。</param>
    ProgramResolution Resolve(string canonicalStation, string programFolder);

    /// <summary>
    /// ファイル名の番組名から、その回の扱いを決める(ADR-0021)。①フォルダ名を含む → <see cref="FilenameProgramVerdict.ContainsFolder"/>、
    /// ②判断の登録がある → その判断、③どちらでもない → <see cref="FilenameProgramVerdict.Unjudged"/>(保留)。
    /// </summary>
    /// <param name="filenameProgram">ファイル名の番組名。null・空なら疑う手がかりが無いので ①。</param>
    FilenameProgramJudgement JudgeFilenameProgram(string canonicalStation, string programFolder, string? filenameProgram);
}

/// <summary>
/// 番組 alias で (正規局, 番組フォルダ名) を正規番組名(= アルバム名)に寄せる。
/// 未登録はフォルダ名にフォールバック。複数フォルダ → 1 アルバムの統合もここで行う。
/// ファイル名の番組名の判断(<see cref="ProgramMasterConfig.FilenamePrograms"/>。ADR-0021)もここで引く。
/// </summary>
public sealed class ProgramResolver : IProgramResolver
{
    private readonly Dictionary<(string Station, string Folder), (string Album, int? PartIndex)> _map = new();

    private readonly Dictionary<(string Station, string Folder, string FilenameProgram), FilenameProgramJudgement> _decisions = new();

    /// <exception cref="InvalidOperationException">
    /// 「別の番組として取り込む」の判断にアルバム名が無い(設定の誤り。取り込みの起動・設定リロードを拒む)。
    /// </exception>
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

        // 同じ組を 2 度書いたら、後に書いた方(Programs の統合と同じ)。
        foreach (var decision in config.FilenamePrograms)
        {
            var match = decision.Match;
            var key = (TextNormalizer.ToMatchKey(match.Station), TextNormalizer.ToMatchKey(match.Folder),
                TextNormalizer.ToMatchKey(match.FilenameProgram));
            _decisions[key] = decision.Treat switch
            {
                FilenameProgramTreatment.SameProgram => new FilenameProgramJudgement(FilenameProgramVerdict.SameProgram),
                FilenameProgramTreatment.Skip => new FilenameProgramJudgement(FilenameProgramVerdict.Skip),
                FilenameProgramTreatment.OtherProgram when !string.IsNullOrWhiteSpace(decision.Album)
                    => new FilenameProgramJudgement(FilenameProgramVerdict.OtherProgram, decision.Album.Trim()),
                FilenameProgramTreatment.OtherProgram => throw new InvalidOperationException(
                    $"programs.json の FilenamePrograms: {match.Station}「{match.Folder}」の「{match.FilenameProgram}」は " +
                    "OtherProgram ですが、Album(アルバム名)がありません"),
                _ => throw new InvalidOperationException(
                    $"programs.json の FilenamePrograms: {match.Station}「{match.Folder}」の「{match.FilenameProgram}」の " +
                    $"Treat({decision.Treat})を解釈できません"),
            };
        }
    }

    public ProgramResolution Resolve(string canonicalStation, string programFolder)
    {
        var key = (TextNormalizer.ToMatchKey(canonicalStation), TextNormalizer.ToMatchKey(programFolder));
        return _map.TryGetValue(key, out var hit)
            ? new ProgramResolution(hit.Album, UsedFallback: false, PartIndex: hit.PartIndex)
            : new ProgramResolution(programFolder.Trim(), UsedFallback: true);
    }

    public FilenameProgramJudgement JudgeFilenameProgram(string canonicalStation, string programFolder, string? filenameProgram)
    {
        var nameKey = TextNormalizer.ToMatchKey(filenameProgram);
        var folderKey = TextNormalizer.ToMatchKey(programFolder);
        if (nameKey.Length == 0 || nameKey.Contains(folderKey, StringComparison.Ordinal))
            return FilenameProgramJudgement.ContainsFolder;

        return _decisions.TryGetValue((TextNormalizer.ToMatchKey(canonicalStation), folderKey, nameKey), out var decided)
            ? decided
            : new FilenameProgramJudgement(FilenameProgramVerdict.Unjudged);
    }
}
