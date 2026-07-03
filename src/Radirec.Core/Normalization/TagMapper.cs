using Radirec.Core.Model;

namespace Radirec.Core.Normalization;

/// <summary>
/// 放送回(<see cref="Episode"/>)を案A の正規化タグ(<see cref="NormalizedTags"/>)へ写像する純粋関数。
/// </summary>
public static class TagMapper
{
    /// <summary>既定ジャンル。</summary>
    public const string DefaultGenre = "ラジオ";

    public static NormalizedTags Map(Episode episode, string genre = DefaultGenre)
    {
        var date = episode.BroadcastDate;
        var mmdd = date.Month * 100 + date.Day; // MMDD(最大 1231)

        // 統合番組の同日パートは track をパート番号で細分化し、アルバム内で part1→part2 の順に並べる。
        // MMDD*10 + パート(パートは 1〜9 想定)。単一番組は MMDD のまま。ushort に収まる。
        var track = episode.PartIndex is int part ? mmdd * 10 + part : mmdd;

        return new NormalizedTags
        {
            AlbumArtist = episode.Station,
            Album = episode.Program,
            Artist = string.IsNullOrWhiteSpace(episode.Artist) ? null : episode.Artist,
            // 同日パートはタイトルにもパートを付し、トラック一覧で区別できるようにする。
            Title = episode.PartIndex is int p ? $"{date:yyyy-MM-dd} ({p})" : date.ToString("yyyy-MM-dd"),
            Date = date,
            // 放送年。ushort に収まる(< 65536)。
            Disc = checked((ushort)date.Year),
            Track = checked((ushort)track),
            Genre = genre,
            Comment = string.IsNullOrWhiteSpace(episode.RecordingKind) ? null : episode.RecordingKind,
        };
    }
}
