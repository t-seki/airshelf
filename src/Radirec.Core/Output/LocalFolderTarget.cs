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

    private void TryDelete(string path)
    {
        try
        {
            if (fileSystem.File.Exists(path))
                fileSystem.File.Delete(path);
        }
        catch
        {
            // 後始末の失敗で元の例外を隠さない。
        }
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

    /// <summary>
    /// 同名衝突が無いときの配置先(<c>{放送局}/{番組}/{番組} YYYY-MM-DD[-N]{ext}</c>)。取り込みの
    /// <see cref="Plan"/> と再適用(ADR-0016)で同じ規則を使うために公開する。
    /// </summary>
    public string PreferredPath(Episode episode, NormalizedTags tags, string extension)
        => fileSystem.Path.Combine(DirectoryFor(tags), BaseNameFor(episode, tags) + extension);

    /// <summary>
    /// 取り込み済みのファイルを <paramref name="newPath"/> へ置き直す(統合設定の再適用。ADR-0016)。
    /// 取り込みと同じく <c>.partial</c> にコピー → <see cref="ITagWriter.WritePlacement"/> で
    /// <c>album</c> / <c>title</c> / <c>track</c> だけ書き換え → リネームで確定する。元のファイルは消さない
    /// (消すのは呼び出し側。確定前に止まっても元が残る)。<paramref name="newPath"/> に既にファイルがあれば
    /// 確定のリネームが失敗する(上書きしない)。
    /// </summary>
    public Task RelocateAsync(
        string currentPath, string newPath, NormalizedTags tags, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(newPath)!);

        var partialPath = PartialPathFor(newPath);
        if (fileSystem.File.Exists(partialPath))
            fileSystem.File.Delete(partialPath);

        fileSystem.File.Copy(currentPath, partialPath, overwrite: false);
        try
        {
            tagWriter.WritePlacement(partialPath, tags);
            fileSystem.File.Move(partialPath, newPath);
        }
        catch
        {
            // 確定できなかった .partial は残さない(残っても次の再適用・起動時の掃除で消えるが、先に片付ける)。
            TryDelete(partialPath);
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 取り込み済みのファイルの <c>album</c> / <c>title</c> / <c>track</c> を、置き場所を変えずに書き換える
    /// (統合設定の再適用で、ファイル名だけ手で直した回のタグを揃える。#141)。同じフォルダの <c>.partial</c> に
    /// コピー → <see cref="ITagWriter.WritePlacement"/> → 元の名前へ置き換えて確定する。確定までは元のファイルに
    /// 触れないので、途中で止まっても元は壊れない。
    /// </summary>
    public Task RewritePlacementAsync(string path, NormalizedTags tags, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var partialPath = PartialPathFor(path);
        if (fileSystem.File.Exists(partialPath))
            fileSystem.File.Delete(partialPath);

        fileSystem.File.Copy(path, partialPath, overwrite: false);
        try
        {
            tagWriter.WritePlacement(partialPath, tags);

            // 確定は上書きのリネーム 1 回で行う。元を消してから置くと、その間に止まったときに元も新しいものも無くなる。
            // File.Move(overwrite) は Windows では MoveFileEx(MOVEFILE_REPLACE_EXISTING)で、同じフォルダの中なら
            // 置き換えを含めた 1 回のリネームとして(SMB でもサーバー側の 1 回のリネーム要求として)扱われる、と考えて
            // これを選んだ。失敗すれば元はそのまま残る(Jellyfin が開いている等)。File.Replace(ReplaceFile)も元を
            // 残す置き換えだが、属性の引き継ぎやバックアップを含む複数段の処理で、SMB の相手によっては失敗が報告されて
            // いるため使わない。NAS の実機での挙動は未確認(#141)。
            fileSystem.File.Move(partialPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }

        return Task.CompletedTask;
    }

    // PartIndex のある回は -N を付して区別する(-1, -2, …)。PartIndex は重複マーカー (N) か、
    // 無ければ統合番組の回のパート(由来フォルダ順)。EpisodeAssembler の source.PartIndex ?? program.PartIndex。
    private static string BaseNameFor(Episode episode, NormalizedTags tags)
    {
        var baseName = $"{FileNameSanitizer.Sanitize(tags.Album)} {tags.Date:yyyy-MM-dd}";
        return episode.PartIndex is int part ? $"{baseName}-{part}" : baseName;
    }

    private OutputPlan ResolveFinalPath(string dir, RecordingFile source, Episode episode, NormalizedTags tags)
    {
        var extension = source.Extension;
        var baseName = BaseNameFor(episode, tags);

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
