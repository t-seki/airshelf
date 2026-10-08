using System.IO.Abstractions;
using Airshelf.Core.Model;

namespace Airshelf.Core.Ingestion;

/// <summary>取り込み対象の録音ファイルを列挙する。</summary>
public interface IRecordingSource
{
    IEnumerable<RecordingFile> Enumerate();
}

/// <summary>
/// <c>{root}/{放送局}/{番組}/{file}</c> 構造のフォルダを走査して <see cref="RecordingFile"/> を作る。
/// 放送局フォルダ = 祖父ディレクトリ名、番組フォルダ = 親ディレクトリ名。
/// 既定で らじれこ(<c>*.m4a</c>)と radikool(<c>*.mp3</c>)の両方を列挙する(importer が振り分ける)。
/// </summary>
public sealed class FileSystemRecordingSource(
    IFileSystem fileSystem, string root, params string[] searchPatterns)
    : IRecordingSource
{
    private readonly string[] _patterns = searchPatterns.Length > 0 ? searchPatterns : ["*.m4a", "*.mp3"];

    public IEnumerable<RecordingFile> Enumerate()
    {
        if (!fileSystem.Directory.Exists(root))
            yield break;

        foreach (var path in _patterns.SelectMany(
                     p => fileSystem.Directory.EnumerateFiles(root, p, SearchOption.AllDirectories)))
        {
            var info = fileSystem.FileInfo.New(path);
            var programDir = fileSystem.Path.GetDirectoryName(path);
            var stationDir = programDir is null ? null : fileSystem.Path.GetDirectoryName(programDir);

            yield return new RecordingFile
            {
                FilePath = path,
                FileName = fileSystem.Path.GetFileName(path),
                ProgramFolder = programDir is null ? string.Empty : fileSystem.Path.GetFileName(programDir),
                StationFolder = stationDir is null ? string.Empty : fileSystem.Path.GetFileName(stationDir),
                Extension = fileSystem.Path.GetExtension(path).ToLowerInvariant(),
                SizeBytes = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
            };
        }
    }
}
