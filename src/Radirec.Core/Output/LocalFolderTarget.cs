using System.IO.Abstractions;
using Radirec.Core.Model;

namespace Radirec.Core.Output;

/// <summary>
/// ローカル/UNC フォルダへ配置する出力先。ディレクトリ <c>{放送局}/{番組}/</c>、ファイル名
/// <c>{番組} {YYYY-MM-DD}.{ext}</c>。パート(統合番組の回)か重複マーカーのある回は <c>-N</c> を付す(<c>… YYYY-MM-DD-1</c>)。
/// 同名ファイルが既にあれば音声部分を比べ、同じ内容なら書かずに取込済みとし、違う内容なら時刻(無ければ
/// <c>2</c>)を付けて別名で書く(ADR-0015)。書き込みは
/// テンポラリ(<c>.partial</c>)→ タグ付与 → 同一ディレクトリ内リネームでアトミックに確定する。
/// </summary>
public sealed class LocalFolderTarget(
    IFileSystem fileSystem, ITagWriter tagWriter, IAudioContentComparer contentComparer, string outputRoot)
    : IOutputTarget
{

    public OutputPlan Plan(RecordingFile source, Episode episode, NormalizedTags tags)
    {
        var dir = DirectoryFor(tags);
        return ResolveFinalPath(dir, source, episode, tags);
    }

    public async Task<string> WriteAsync(
        RecordingFile source, OutputPlan plan, NormalizedTags tags,
        CancellationToken cancellationToken = default)
    {
        // 同じ内容が既にある回は呼び出し側が書かずに済ませる契約(上書きで既存を壊さないための番人)。
        if (plan.AlreadyPresent)
            throw new InvalidOperationException($"同じ内容のファイルが既にあります: {plan.Path}");

        var finalPath = plan.Path;
        fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(finalPath)!);

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

    private OutputPlan ResolveFinalPath(string dir, RecordingFile source, Episode episode, NormalizedTags tags)
    {
        var extension = source.Extension;
        var baseName = $"{FileNameSanitizer.Sanitize(tags.Album)} {tags.Date:yyyy-MM-dd}";

        // PartIndex のある回は -N を付して区別する(-1, -2, …)。PartIndex は重複マーカー (N) か、
        // 無ければ統合番組の回のパート(由来フォルダ順)。EpisodeAssembler の source.PartIndex ?? program.PartIndex。
        if (episode.PartIndex is int part)
            baseName = $"{baseName}-{part}";

        var candidate = fileSystem.Path.Combine(dir, baseName + extension);
        if (!fileSystem.File.Exists(candidate))
            return new OutputPlan { Path = candidate };

        // 同名が既にある。台帳を失った後の再走査や size/mtime の変わったコピーでは、同じ回の取込済み
        // ファイルがここに来る。音声部分が同じなら書かない(near-dup を作らない。ADR-0015)。
        if (contentComparer.HasSameAudio(source.FilePath, candidate))
            return new OutputPlan { Path = candidate, AlreadyPresent = true };

        // 違う内容(想定外の同日衝突 = パート番号の無い同日別放送等)の保険: 時刻(あれば)で区別。
        // 以前の実装が作った時刻付きの near-dup が既にあれば、それとも比べて 3 つ目を作らない。
        var suffix = episode.BroadcastTime is { } t ? t.ToString("HHmm") : "2";
        var alternate = fileSystem.Path.Combine(dir, $"{baseName} {suffix}{extension}");
        if (fileSystem.File.Exists(alternate) && contentComparer.HasSameAudio(source.FilePath, alternate))
            return new OutputPlan { Path = alternate, AlreadyPresent = true };

        return new OutputPlan { Path = alternate, CollidedWith = candidate };
    }
}
