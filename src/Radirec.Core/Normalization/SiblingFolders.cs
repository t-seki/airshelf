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

    private static readonly Regex Digits = new("[0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>フォルダ名から末尾のパート・時間帯の印を除いた名前(NFKC 済み)。</summary>
    public static string BaseName(string folder)
    {
        var nfkc = folder.Normalize(NormalizationForm.FormKC).Trim();
        return TrailingMarker.Replace(nfkc, string.Empty, 1).TrimEnd();
    }

    /// <summary>
    /// フォルダ名から末尾の印を除いた名前を、**元の表記のまま**返す(統合設定の提案のアルバム名。#144)。
    /// <see cref="BaseName"/> は NFKC 済みなので、全角の <c>！</c> などが半角に変わってしまう。印の無いフォルダの回は
    /// フォルダ名そのもの(元の表記)がアルバム名になっているので、提案もそれに揃える(揃わないと既存の回が動く)。
    /// </summary>
    /// <remarks>
    /// 印の規則は <see cref="BaseName"/> だけに置く。元の名前の先頭から、NFKC にすると <see cref="BaseName"/> と同じに
    /// なる一番短い部分を探して返す。見つからない(NFKC で文字数が変わる並びが境目にある等)ときは <see cref="BaseName"/>。
    /// </remarks>
    public static string OriginalBaseName(string folder)
    {
        var original = folder.Trim();
        var baseName = BaseName(folder);
        for (var length = 0; length <= original.Length; length++)
        {
            // サロゲートペアの途中では切らない(Normalize が不正な文字列として投げる)。
            if (length > 0 && length < original.Length && char.IsLowSurrogate(original[length]))
                continue;

            var prefix = original[..length];
            if (prefix.Normalize(NormalizationForm.FormKC).TrimEnd() == baseName)
                return prefix.TrimEnd();
        }
        return baseName;
    }

    /// <summary>
    /// 末尾の印の中の最初の数字(<c>(2)</c> → 2、<c>Part1</c> → 1、<c>8時9時</c> → 8、<c>10時1</c> → 10)。印が無ければ null。
    /// </summary>
    public static int? MarkerNumber(string folder)
    {
        var nfkc = folder.Normalize(NormalizationForm.FormKC).Trim();
        var marker = TrailingMarker.Match(nfkc);
        if (!marker.Success)
            return null;

        var digits = Digits.Match(marker.Value);
        return int.TryParse(digits.Value, out var number) ? number : int.MaxValue;
    }

    /// <summary>
    /// 兄弟フォルダをパートの順に並べる(統合設定の提案の <c>Match.Folders</c> の並び = パート番号。#144)。
    /// 印の無いフォルダを先頭に、あとは印の中の数字(<see cref="MarkerNumber"/>)の小さい順。文字列順ではない
    /// (<c>8時9時</c> → <c>9時10時</c> → <c>10時11時</c>)。数字が同じなら名前の序数順。
    /// </summary>
    public static IReadOnlyList<string> OrderAsParts(IEnumerable<string> folders)
        => folders
            .Select(f => (Folder: f, Number: MarkerNumber(f)))
            .OrderBy(x => x.Number.HasValue)
            .ThenBy(x => x.Number ?? 0)
            .ThenBy(x => x.Folder, StringComparer.Ordinal)
            .Select(x => x.Folder)
            .ToList();

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
