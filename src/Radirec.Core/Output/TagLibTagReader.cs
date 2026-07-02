namespace Radirec.Core.Output;

/// <summary>TagLib# による <see cref="ITagReader"/> 実装。</summary>
public sealed class TagLibTagReader : ITagReader
{
    public string? ReadArtist(string filePath)
    {
        using var file = TagLib.File.Create(filePath);
        var artist = file.Tag.FirstPerformer;
        return string.IsNullOrWhiteSpace(artist) ? null : artist;
    }
}
