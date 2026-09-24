using Radirec.Core.Model;
using TagLib;

namespace Radirec.Core.Output;

/// <summary>
/// TagLib# による <see cref="ITagWriter"/> 実装。MP4(M4A / らじれこ)と MP3(ID3 / radikool)の
/// 両方へ案A タグを書き込む。
/// </summary>
/// <remarks>
/// 汎用タグ(album/albumartist/title/genre/disc/track 等)は両形式共通。フル ISO 日付
/// (YYYY-MM-DD)だけ形式別に書く: MP4 は <c>©day</c> アトム、MP3 は ID3v2 の <c>TDRC</c> フレーム。
/// TagLib の汎用 <c>Tag.Year</c> は 4 桁年しか持てず日付を年だけに潰すため使わない。
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
        // null 代入では既存フレームが消えない実装があるため空文字でクリアする(radikool の
        // 元コメント "YYYY年MM月DD日" が残らないように)。案A の comment は録音種別 or 空。
        tag.Comment = tags.Comment ?? string.Empty;
        tag.Disc = tags.Disc;
        tag.Track = tags.Track;

        var iso = tags.Date.ToString("yyyy-MM-dd");

        if (file.GetTag(TagTypes.Apple, create: true) is TagLib.Mpeg4.AppleTag apple)
        {
            apple.SetText(DayAtom, iso);
        }

        if (file.GetTag(TagTypes.Id3v2, create: true) is TagLib.Id3v2.Tag id3)
        {
            // TDRC(Recording time)へフル日付を直接書く(Tag.Year の年潰しを避ける)。
            id3.SetTextFrame("TDRC", iso);

            // 案A の comment が空(radikool)なら、元ソースのコメントフレームを除去する
            // (汎用 Comment への null/空 代入では既存フレームが消えないため)。radikool(旧 Lavf)は
            // コメントを標準 COMM ではなく TXXX(description="comment")で書くので両方を掃除する。
            if (string.IsNullOrEmpty(tags.Comment))
            {
                id3.RemoveFrames("COMM");
                foreach (var f in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>()
                             .Where(f => string.Equals(f.Description, "comment", StringComparison.OrdinalIgnoreCase))
                             .ToList())
                {
                    id3.RemoveFrame(f);
                }
            }
        }

        file.Save();
    }

    public void WritePlacement(string filePath, NormalizedTags tags)
    {
        using var file = TagLib.File.Create(filePath);

        var tag = file.Tag;
        tag.Album = tags.Album;
        tag.Title = tags.Title;
        tag.Track = tags.Track;

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
