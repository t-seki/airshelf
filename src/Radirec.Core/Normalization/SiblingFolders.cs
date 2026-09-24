using System.Text;
using System.Text.RegularExpressions;

namespace Radirec.Core.Normalization;

/// <summary>番組フォルダ 1 つ(放送局の正規名 + らじれこの番組フォルダ名)。</summary>
public sealed record ProgramFolder(string Station, string Folder);

/// <summary>
/// 兄弟フォルダの 1 組。同じ放送局で、パートや時間帯の印を除くと同じ名前になるフォルダ(2 つ以上)。
/// </summary>
/// <param name="BaseName">印を除いた名前(NFKC 済み。表示用)。</param>
/// <param name="Folders">組に入るフォルダ名(元の表記のまま、序数順)。</param>
public sealed record SiblingFolderGroup(string Station, string BaseName, IReadOnlyList<string> Folders);

/// <summary>
/// パートに分かれて録音された番組(<c>X(1)</c> / <c>X(2)</c>、<c>X Part1</c>、<c>X 8時9時</c> など)の
/// 兄弟フォルダを見つける。統合設定(<c>programs.json</c>)が要りそうな番組を知らせるために使う
/// (再適用の dry-run の結果・取込通知。ADR-0016 / #127)。フォルダ名のパターンから自動で統合はしない。
/// </summary>
/// <remarks>
/// 名前は NFKC(全角の括弧・数字・英字を半角へ)にしてから、末尾の印を 1 つだけ除く:
/// <c>(N)</c> / <c>PartN</c> / <c>パートN</c> / 時間帯(<c>8時9時</c>・<c>10時1</c>。<c>([0-9]+時)+[0-9]*</c>)と、
/// その前の空白。除いた名前を照合キー(<see cref="TextNormalizer.ToMatchKey"/>)にして比べる。
/// 特番の <c>… 17時50分～19時</c> は <c>19時</c> だけが除かれ、残りの <c>17時50分～</c> が番組ごとに違うので兄弟にならない。
/// </remarks>
public static class SiblingFolders
{
    private static readonly Regex TrailingMarker = new(
        @"\s*(?:\([0-9]+\)|Part[0-9]+|パート[0-9]+|(?:[0-9]+時)+[0-9]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>フォルダ名から末尾のパート・時間帯の印を除いた名前(NFKC 済み)。</summary>
    public static string BaseName(string folder)
    {
        var nfkc = folder.Normalize(NormalizationForm.FormKC).Trim();
        return TrailingMarker.Replace(nfkc, string.Empty, 1).TrimEnd();
    }

    /// <summary>
    /// <paramref name="folders"/> の中から兄弟フォルダの組をすべて返す(放送局・名前の序数順)。
    /// 同じフォルダが重複して渡されても 1 つに数える。
    /// </summary>
    public static IReadOnlyList<SiblingFolderGroup> Find(IEnumerable<ProgramFolder> folders)
        => folders
            .DistinctBy(f => (f.Station, f.Folder))
            .Select(f => (Folder: f, Base: BaseName(f.Folder)))
            .Where(x => x.Base.Length > 0)
            .GroupBy(x => (Station: TextNormalizer.ToMatchKey(x.Folder.Station), Key: TextNormalizer.ToMatchKey(x.Base)))
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var first = g.OrderBy(x => x.Folder.Folder, StringComparer.Ordinal).First();
                return new SiblingFolderGroup(
                    first.Folder.Station,
                    first.Base,
                    g.Select(x => x.Folder.Folder).Order(StringComparer.Ordinal).ToList());
            })
            .OrderBy(g => g.Station, StringComparer.Ordinal)
            .ThenBy(g => g.BaseName, StringComparer.Ordinal)
            .ToList();
}
