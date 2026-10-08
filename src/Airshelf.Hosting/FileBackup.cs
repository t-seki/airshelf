namespace Airshelf.Hosting;

/// <summary>
/// 書き換える前のバックアップ <c>{path}.bak-YYYYMMDD-HHmmss</c> を同じフォルダに作る
/// (programs.json への追記 #144 と、全体の書き直し #153 で共有)。
/// </summary>
internal static class FileBackup
{
    /// <summary>
    /// <paramref name="content"/>(書き換える前の中身)をバックアップとして置く。同じ秒のバックアップが既にあれば
    /// <c>-2</c>, <c>-3</c> … を付ける(前のバックアップを上書きしない)。
    /// <see cref="NewFileWriter"/> で一時ファイルに書き切ってから置くので、NAS への書き込みが途中で切れても、
    /// 壊れたバックアップが正式な名前で残らない(「バックアップで置き換えれば戻る」を保つ)。
    /// </summary>
    /// <returns>作ったバックアップのパス。</returns>
    /// <exception cref="Exception">書き込みの失敗はそのまま投げる(呼び出し側は本体を書き換えない)。</exception>
    public static string Create(string path, byte[] content, DateTime now)
    {
        var stem = $"{path}.bak-{now:yyyyMMdd-HHmmss}";
        var candidate = stem;
        for (var n = 2; ; n++)
        {
            if (NewFileWriter.CreateIfAbsent(candidate, s => s.Write(content)))
                return candidate;
            candidate = $"{stem}-{n}"; // 既にある(または置く直前に誰かが作った)。別名にする
        }
    }
}
