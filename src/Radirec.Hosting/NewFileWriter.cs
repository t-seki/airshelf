namespace Radirec.Hosting;

/// <summary>
/// 無いファイルだけを作る(既存は温存)。中身は同じフォルダの一時ファイルに書き切ってから、上書きしない形で置く。
/// トレイの「設定ファイルを開く」(#143)が設定ファイルを見本から作るのに使う。
/// <para>
/// 置き先に直接書かないのが要点。<c>ProgramsFile</c> が NAS を指していて書き込みの途中で切れると、途中までの壊れた
/// JSON が残り、「既にある」ので以後も作り直されず、設定リロードは不正 JSON で失敗し続けるため。
/// </para>
/// </summary>
public static class NewFileWriter
{
    /// <summary>
    /// <paramref name="path"/> が無ければ <paramref name="write"/> で中身を書いて作る。親フォルダも作る。
    /// </summary>
    /// <returns>作ったなら true。既にあった(確かめたときにあった、または置く直前に誰かが作った)なら false。</returns>
    /// <exception cref="Exception">書き込み・置く段階の失敗はそのまま投げる。一時ファイルは消し、<paramref name="path"/> は作らない。</exception>
    public static bool CreateIfAbsent(string path, Action<Stream> write)
    {
        if (File.Exists(path))
            return false;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);

        // 同じフォルダに置く(別ボリュームだと Move がコピー + 削除になり、途中で切れうる)。
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                write(stream);

            try
            {
                File.Move(temp, path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false; // 確かめた後に誰かが作った。上書きせず既存を温存する(一時ファイルは finally で消す)
            }
        }
        finally
        {
            TryDelete(temp); // 置けていれば一時ファイルはもう無い
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // 消せなくても本来の結果(成功・失敗)は変えない。残るのは隠し名の一時ファイルだけ。
        }
    }
}
