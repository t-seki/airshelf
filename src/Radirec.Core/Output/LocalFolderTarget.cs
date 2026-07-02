using System.IO.Abstractions;
using Radirec.Core.Model;

namespace Radirec.Core.Output;

/// <summary>
/// ローカル/UNC フォルダへ配置する出力先。ディレクトリ <c>{放送局}/{番組}/</c>、ファイル名
/// <c>{番組} {YYYY-MM-DD}.{ext}</c>(衝突時のみ時刻付与)。書き込みは
/// テンポラリ(<c>.partial</c>)→ タグ付与 → 同一ディレクトリ内リネームでアトミックに確定する。
/// </summary>
public sealed class LocalFolderTarget(IFileSystem fileSystem, ITagWriter tagWriter, string outputRoot)
    : IOutputTarget
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

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

    private string DirectoryFor(NormalizedTags tags)
        => fileSystem.Path.Combine(outputRoot, Sanitize(tags.AlbumArtist), Sanitize(tags.Album));

    private string PartialPathFor(string finalPath)
    {
        var dir = fileSystem.Path.GetDirectoryName(finalPath)!;
        var name = fileSystem.Path.GetFileNameWithoutExtension(finalPath);
        var ext = fileSystem.Path.GetExtension(finalPath);
        return fileSystem.Path.Combine(dir, $"{name}.partial{ext}");
    }

    private string ResolveFinalPath(string dir, Episode episode, NormalizedTags tags, string extension)
    {
        var baseName = $"{Sanitize(tags.Album)} {tags.Date:yyyy-MM-dd}";
        var candidate = fileSystem.Path.Combine(dir, baseName + extension);
        if (!fileSystem.File.Exists(candidate))
            return candidate;

        // 同日別2回の衝突: 時刻(あれば)で区別。
        var suffix = episode.BroadcastTime is { } t ? t.ToString("HHmm") : "2";
        return fileSystem.Path.Combine(dir, $"{baseName} {suffix}{extension}");
    }

    private static string Sanitize(string segment)
    {
        var cleaned = string.Join('_', segment.Split(InvalidChars)).Trim();
        return cleaned.Length == 0 ? "_" : cleaned;
    }
}
