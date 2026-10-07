using Airshelf.Core.Model;

namespace Airshelf.Core.Sources;

/// <summary>
/// 複数の <see cref="ISourceImporter"/> を <see cref="ISourceImporter.CanHandle"/> で振り分ける
/// ルータ。1 回の取込で らじれこ(M4A)と radikool(MP3)が混在しても、ファイルごとに適切な
/// importer を選ぶ。登録順に最初に扱える importer を採用する。
/// </summary>
public sealed class SourceRouter(IEnumerable<ISourceImporter> importers) : ISourceImporter
{
    private readonly IReadOnlyList<ISourceImporter> _importers = importers.ToArray();

    public bool CanHandle(RecordingFile file) => _importers.Any(i => i.CanHandle(file));

    public ParsedSource Parse(RecordingFile file)
    {
        foreach (var importer in _importers)
        {
            if (importer.CanHandle(file))
                return importer.Parse(file);
        }

        throw new SourceParseException($"扱える importer がありません: {file.FileName}");
    }
}
