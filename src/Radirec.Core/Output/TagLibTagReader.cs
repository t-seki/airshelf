namespace Radirec.Core.Output;

/// <summary>TagLib# による <see cref="ITagReader"/> / <see cref="IPlacementTagReader"/> 実装。</summary>
public sealed class TagLibTagReader : ITagReader, IPlacementTagReader
{
    public PlacementTags ReadPlacement(string filePath)
    {
        using var file = TagLib.File.Create(filePath);
        return new PlacementTags(file.Tag.Album, file.Tag.Title, file.Tag.Track);
    }

    public string? ReadArtist(string filePath)
    {
        using var file = TagLib.File.Create(filePath);
        return Clean(file.Tag.FirstPerformer);
    }

    public RawTags ReadTags(string filePath)
    {
        using var file = TagLib.File.Create(filePath);
        return new RawTags(Clean(file.Tag.FirstGenre), Clean(file.Tag.FirstPerformer));
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
