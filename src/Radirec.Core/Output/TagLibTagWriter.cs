using Radirec.Core.Model;
using TagLib;

namespace Radirec.Core.Output;

/// <summary>
/// TagLib# による <see cref="ITagWriter"/> 実装。MP4(M4A)へ案A タグを書き込む。
/// </summary>
/// <remarks>
/// date はフル ISO(YYYY-MM-DD)を MP4 の <c>©day</c> アトムへ直接書く。
/// TagLib の汎用 <c>Tag.Year</c> は 4 桁年しか持てず <c>©day</c> を年だけで上書きしてしまうため、
/// <c>Tag.Year</c> は設定しない。
/// </remarks>
public sealed class TagLibTagWriter : ITagWriter
{
    // MP4 の日付アトム "©day"(先頭バイトは 0xA9 = '©')。文字列経由だと UTF-8 で 2 バイトに
    // 化けるためバイト列で厳密に構築する。
    private static readonly ByteVector DayAtom = new([0xA9, (byte)'d', (byte)'a', (byte)'y']);

    public void Write(string filePath, NormalizedTags tags)
    {
        using var file = TagLib.File.Create(filePath);

        var tag = file.Tag;
        tag.Album = tags.Album;
        tag.AlbumArtists = [tags.AlbumArtist];
        tag.Performers = tags.Artist is null ? [] : [tags.Artist];
        tag.Title = tags.Title;
        tag.Genres = [tags.Genre];
        tag.Comment = tags.Comment;
        tag.Disc = tags.Disc;
        tag.Track = tags.Track;

        if (file.GetTag(TagTypes.Apple, create: true) is TagLib.Mpeg4.AppleTag apple)
        {
            apple.SetText(DayAtom, tags.Date.ToString("yyyy-MM-dd"));
        }

        file.Save();
    }

    /// <summary>書き込んだフル ISO 日付(<c>©day</c>)を読み戻す。検証・確認用。</summary>
    public static string? ReadIsoDate(string filePath)
    {
        using var file = TagLib.File.Create(filePath);
        if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
        {
            var values = apple.GetText(DayAtom);
            return values.Length > 0 ? values[0] : null;
        }

        return null;
    }
}
