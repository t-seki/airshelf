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
    /// ② で複数の判断が当たったら、完全一致 → 前方一致の照合キーが長い方 → 部分一致の照合キーが長い方、同着は後に書いた方(#240)。
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

    // ファイル名の番組名の判断を (局, フォルダ) ごとに、programs.json に書いた順で持つ(照合と優先は FilenameProgramMatcher。#240)。
    private readonly Dictionary<(string Station, string Folder), List<Decided>> _decisions = new();

    private sealed record Decided(FilenameProgramMatchMode Mode, string Key, FilenameProgramJudgement Judgement);

    /// <exception cref="ProgramsConfigException">
    /// 判断の設定の誤り(壊れた programs.json と同じく、取り込みの起動は縮退し、設定リロードでは新しい設定に切り替わらない
    /// 〈前の設定のまま動く〉):「別の番組として取り込む」の判断にアルバム名が無い / 照合の種類(<c>Mode</c>)を解釈できない /
    /// 前方一致・部分一致の判断の照合キーが空(フォルダのすべての回に当たってしまう。#240)。
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

        // 同じ組(種類・照合キーも同じ)を 2 度書いたら、後に書いた方(Programs の統合と同じ。優先の同着は後に書いた方)。
        foreach (var decision in config.FilenamePrograms)
        {
            var match = decision.Match;
            if (match.Mode is not (FilenameProgramMatchMode.Exact or FilenameProgramMatchMode.Prefix or FilenameProgramMatchMode.Contains))
                throw new ProgramsConfigException(
                    $"programs.json の FilenamePrograms: {match.Station}「{match.Folder}」の「{match.FilenameProgram}」の " +
                    $"Mode({match.Mode})を解釈できません");

            // 照合キーが空の前方一致・部分一致は、フォルダのすべての回に当たってしまう(#240)。
            var key = TextNormalizer.ToMatchKey(match.FilenameProgram);
            if (match.Mode != FilenameProgramMatchMode.Exact && key.Length == 0)
                throw new ProgramsConfigException(
                    $"programs.json の FilenamePrograms: {match.Station}「{match.Folder}」の {match.Mode} の判断に、" +
                    "照合する FilenameProgram がありません");

            var judgement = decision.Treat switch
            {
                FilenameProgramTreatment.SameProgram => new FilenameProgramJudgement(FilenameProgramVerdict.SameProgram),
                FilenameProgramTreatment.Skip => new FilenameProgramJudgement(FilenameProgramVerdict.Skip),
                FilenameProgramTreatment.OtherProgram when !string.IsNullOrWhiteSpace(decision.Album)
                    => new FilenameProgramJudgement(FilenameProgramVerdict.OtherProgram, decision.Album.Trim()),
                FilenameProgramTreatment.OtherProgram => throw new ProgramsConfigException(
                    $"programs.json の FilenamePrograms: {match.Station}「{match.Folder}」の「{match.FilenameProgram}」は " +
                    "OtherProgram ですが、Album(アルバム名)がありません"),
                _ => throw new ProgramsConfigException(
                    $"programs.json の FilenamePrograms: {match.Station}「{match.Folder}」の「{match.FilenameProgram}」の " +
                    $"Treat({decision.Treat})を解釈できません"),
            };

            var place = (TextNormalizer.ToMatchKey(match.Station), TextNormalizer.ToMatchKey(match.Folder));
            if (!_decisions.TryGetValue(place, out var list))
                _decisions[place] = list = [];
            list.Add(new Decided(match.Mode, key, judgement));
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

        // 優先は 完全一致 → 前方一致の長い方 → 部分一致の長い方、同着は後に書いた方(#240。FilenameProgramMatcher)。
        return _decisions.TryGetValue((TextNormalizer.ToMatchKey(canonicalStation), folderKey), out var decisions)
               && FilenameProgramMatcher.FindBest(decisions, d => d.Mode, d => d.Key, nameKey) is { } decided
            ? decided.Judgement
            : new FilenameProgramJudgement(FilenameProgramVerdict.Unjudged);
    }
}
