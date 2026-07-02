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

        return new NormalizedTags
        {
            AlbumArtist = episode.Station,
            Album = episode.Program,
            Artist = string.IsNullOrWhiteSpace(episode.Artist) ? null : episode.Artist,
            Title = date.ToString("yyyy-MM-dd"),
            Date = date,
            // 放送年。ushort に収まる(< 65536)。
            Disc = checked((ushort)date.Year),
            // MMDD(月*100+日、最大 1231)。ushort ゆえ YYYYMMDD 統合は不可能。
            Track = checked((ushort)(date.Month * 100 + date.Day)),
            Genre = genre,
            Comment = string.IsNullOrWhiteSpace(episode.RecordingKind) ? null : episode.RecordingKind,
        };
    }
}
