namespace Radirec.Core.Output;

/// <summary>録音ファイルから読み取った生タグ(識別・補完に使う)。</summary>
/// <param name="Genre">ジャンル(radikool は放送局が入る)。無ければ null。</param>
/// <param name="Artist">出演者。無ければ null。</param>
public sealed record RawTags(string? Genre, string? Artist);

/// <summary>録音ファイルから既存タグを読む。</summary>
public interface ITagReader
{
    /// <summary>出演者(artist)。無ければ null。らじれこの識別後の遅延補完用。</summary>
    string? ReadArtist(string filePath);

    /// <summary>genre + artist を 1 回のオープンで読む。radikool の識別(局=genre)用。</summary>
    RawTags ReadTags(string filePath);
}
