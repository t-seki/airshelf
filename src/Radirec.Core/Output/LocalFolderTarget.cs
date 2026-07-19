using System.IO.Abstractions;
using Radirec.Core.Model;

namespace Radirec.Core.Output;

/// <summary>
/// ローカル/UNC フォルダへ配置する出力先。ディレクトリ <c>{放送局}/{番組}/</c>、ファイル名
/// <c>{番組} {YYYY-MM-DD}.{ext}</c>。統合番組の同日パートは <c>-N</c> を付す(<c>… YYYY-MM-DD-1</c>)。
/// パート番号の無い想定外の同日衝突は時刻を保険に付す。書き込みは
/// テンポラリ(<c>.partial</c>)→ タグ付与 → 同一ディレクトリ内リネームでアトミックに確定する。
/// </summary>
public sealed class LocalFolderTarget(IFileSystem fileSystem, ITagWriter tagWriter, string outputRoot)
    : IOutputTarget
{

    public string PlanPath(RecordingFile source, Episode episode, NormalizedTags tags)
    {
        var dir = DirectoryFor(tags);
        return ResolveFinalPath(dir, episode, tags, source.Extension);
    }

    public async Task<string> WriteAsync(
        RecordingFile source, Episode episode, NormalizedTags tags,
        CancellationToken cancellationToken = default)
    {
        var dir = DirectoryFor(tags);
        fileSystem.Directory.CreateDirectory(dir);

        var finalPath = ResolveFinalPath(dir, episode, tags, source.Extension);
        // .partial は拡張子(.m4a)を保つ。TagLib は拡張子で形式判定するため
        // "foo.m4a.partial" だと書き込めない。→ "foo.partial.m4a"。
        var partialPath = PartialPathFor(finalPath);

        // 中断で残った .partial があれば掃除してからやり直す。
        if (fileSystem.File.Exists(partialPath))
            fileSystem.File.Delete(partialPath);

        // 1) 同一ディレクトリへ非破壊コピー(クロスボリューム move を避けアトミック性を担保)
        fileSystem.File.Copy(source.FilePath, partialPath, overwrite: false);

        // 2) コピー先にタグを書く(不完全タグの最終ファイルを残さないためリネーム前に書く)
        tagWriter.Write(partialPath, tags);

        // 3) 同一ディレクトリ内リネームで確定(アトミック)
        fileSystem.File.Move(partialPath, finalPath);

        await Task.CompletedTask;
        return finalPath;
    }

    public int CleanupOrphans()
    {
        if (!fileSystem.Directory.Exists(outputRoot))
            return 0;

        var count = 0;
        foreach (var file in fileSystem.Directory.EnumerateFiles(outputRoot, "*.partial.*", SearchOption.AllDirectories))
        {
            // 検索パターンの曖昧さを避け、命名規約(".partial." を含む)を明示的に確認。
            if (!fileSystem.Path.GetFileName(file).Contains(".partial."))
                continue;
            try
            {
                fileSystem.File.Delete(file);
                count++;
            }
            catch
            {
                // 掃除失敗は致命的でない(次回の書き込みで上書き・再試行される)。
            }
        }

        return count;
    }

    private string DirectoryFor(NormalizedTags tags)
        => fileSystem.Path.Combine(
            outputRoot, FileNameSanitizer.Sanitize(tags.AlbumArtist), FileNameSanitizer.Sanitize(tags.Album));

    private string PartialPathFor(string finalPath)
    {
        var dir = fileSystem.Path.GetDirectoryName(finalPath)!;
        var name = fileSystem.Path.GetFileNameWithoutExtension(finalPath);
        var ext = fileSystem.Path.GetExtension(finalPath);
        return fileSystem.Path.Combine(dir, $"{name}.partial{ext}");
    }

    private string ResolveFinalPath(string dir, Episode episode, NormalizedTags tags, string extension)
    {
        var baseName = $"{FileNameSanitizer.Sanitize(tags.Album)} {tags.Date:yyyy-MM-dd}";

        // 複数フォルダ統合番組の同日パートは、由来フォルダ順のパート番号で区別する(-1, -2, …)。
        if (episode.PartIndex is int part)
            baseName = $"{baseName}-{part}";

        var candidate = fileSystem.Path.Combine(dir, baseName + extension);
        if (!fileSystem.File.Exists(candidate))
            return candidate;

        // 想定外の同日衝突(パート番号の無い同日別放送等)の保険: 時刻(あれば)で区別。
        var suffix = episode.BroadcastTime is { } t ? t.ToString("HHmm") : "2";
        return fileSystem.Path.Combine(dir, $"{baseName} {suffix}{extension}");
    }
}
