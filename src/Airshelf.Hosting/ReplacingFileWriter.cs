namespace Airshelf.Hosting;

/// <summary>
/// 既にあるファイルの中身を置き換える。<see cref="NewFileWriter"/> と同じく、同じフォルダの一時ファイルに書き切ってから
/// 置き換える(programs.json への統合設定の追記。#144)。
/// <para>
/// 置き先に直接書かないのが要点。<c>ProgramsFile</c> が NAS を指していて書き込みの途中で切れても、元のファイルは
/// そのまま残る(途中までの壊れた JSON で設定リロードが失敗し続けることが無い)。
/// </para>
/// </summary>
public static class ReplacingFileWriter
{
    /// <summary><paramref name="path"/> の中身を <paramref name="write"/> で書いたものに置き換える。</summary>
    /// <exception cref="Exception">
    /// 書き込み・置き換えの段階の失敗はそのまま投げる。一時ファイルは消し、<paramref name="path"/> は元のまま残す。
    /// </exception>
    public static void Replace(string path, Action<Stream> write)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();

        // 同じフォルダに置く(別ボリュームだと Move がコピー + 削除になり、途中で切れうる)。
        // File.Replace は使わない(SMB の共有では失敗することがある)。
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                write(stream);

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            NewFileWriter.TryDelete(temp); // 置き換えられていれば一時ファイルはもう無い
        }
    }
}
