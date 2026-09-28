using Radirec.Core.Configuration;

namespace Radirec.Core.Normalization;

/// <summary>
/// ファイル名の番組名の判断の照合と優先(ADR-0021・#240)。取り込みの判定(<see cref="ProgramResolver"/>)と、管理画面の下書きの
/// 判断を要対応の組に当てる計算が、同じ規則を使うためにここに置く。
/// </summary>
public static class FilenameProgramMatcher
{
    /// <summary>
    /// 照合キー <paramref name="nameKey"/> のファイル名の番組名に当たる判断のうち、最も優先するものを返す。無ければ既定値。
    /// 優先は 完全一致 → 前方一致の照合キーが長い方 → 部分一致の照合キーが長い方(種類をまたいで長さは比べない)。
    /// 種類も長さも同じなら、後に並んでいる方(後に書いた方)。
    /// </summary>
    /// <param name="candidates">同じ (局, フォルダ) の判断。programs.json に書いた順に並べる。</param>
    /// <param name="mode">判断の照合の種類。</param>
    /// <param name="key">判断の照合キー(<see cref="TextNormalizer.ToMatchKey"/> 済み)。</param>
    /// <param name="nameKey">ファイル名の番組名の照合キー。</param>
    public static T? FindBest<T>(
        IEnumerable<T> candidates, Func<T, FilenameProgramMatchMode> mode, Func<T, string> key, string nameKey)
    {
        var found = false;
        T? best = default;
        var bestMode = FilenameProgramMatchMode.Exact;
        var bestLength = 0;
        foreach (var candidate in candidates)
        {
            var candidateMode = mode(candidate);
            var candidateKey = key(candidate);
            if (!Matches(candidateMode, candidateKey, nameKey))
                continue;

            // 後に並んでいる方を優先するので、同着でも置き換える。
            if (!found || Rank(candidateMode) < Rank(bestMode)
                || (candidateMode == bestMode && candidateKey.Length >= bestLength))
            {
                found = true;
                best = candidate;
                bestMode = candidateMode;
                bestLength = candidateKey.Length;
            }
        }
        return best;
    }

    /// <summary>照合キー <paramref name="key"/> の判断が、照合キー <paramref name="nameKey"/> のファイル名の番組名に当たるか。</summary>
    public static bool Matches(FilenameProgramMatchMode mode, string key, string nameKey) => mode switch
    {
        FilenameProgramMatchMode.Exact => nameKey == key,
        FilenameProgramMatchMode.Prefix => key.Length > 0 && nameKey.StartsWith(key, StringComparison.Ordinal),
        FilenameProgramMatchMode.Contains => key.Length > 0 && nameKey.Contains(key, StringComparison.Ordinal),
        _ => false,
    };

    private static int Rank(FilenameProgramMatchMode mode) => mode switch
    {
        FilenameProgramMatchMode.Exact => 0,
        FilenameProgramMatchMode.Prefix => 1,
        _ => 2,
    };
}
