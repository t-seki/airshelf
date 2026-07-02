namespace Radirec.Core.Output;

/// <summary>録音ファイルから既存タグを読む(出演者の補完用)。</summary>
public interface ITagReader
{
    /// <summary>出演者(artist)。無ければ null。</summary>
    string? ReadArtist(string filePath);
}
