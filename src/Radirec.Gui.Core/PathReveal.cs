namespace Radirec.Gui.Core;

/// <summary>
/// 台帳が持つ Windows パス(ローカル / UNC)を分解する純関数(#87)。エクスプローラーで開く導線と、
/// 取込履歴のファイル名表示が共有する。
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="System.IO.Path"/> を使わない</b>のが要点。このアセンブリはクロスプラットフォームで
/// 単体テストは Linux 上で走るが、台帳に入っているのは常に Windows パスである。Linux 上の
/// <c>Path.GetFileName(@"I:\radio\a.m4a")</c> は区切りを見つけられず文字列全体を返すため、
/// <c>Path.*</c> に頼るとテストが Linux の挙動を焼き込み、実機(Windows)で誤動作する
/// (同種の罠を #82 で踏んでいる)。よって区切り文字を明示して自前で分解する。
/// </para>
/// </remarks>
public static class PathReveal
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// パス末尾の要素(ファイル名)。区切りを含まない値や空はそのまま返す。
    /// </summary>
    public static string FileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        var trimmed = path.TrimEnd(Separators);
        if (trimmed.Length == 0)
            return path; // 区切りだけのパス(ルート)は分解しない

        var i = trimmed.LastIndexOfAny(Separators);
        return i < 0 ? trimmed : trimmed[(i + 1)..];
    }

    /// <summary>
    /// 1 つ上のディレクトリ。これ以上遡っても「開ける」ディレクトリにならない位置では null を返す。
    /// 具体的には区切りを含まない値、ドライブルート(<c>I:\</c>)、UNC のサーバルート(<c>\\nas</c>。
    /// 共有名を欠くため開けない)で止まる。
    /// </summary>
    public static string? Parent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var trimmed = path.TrimEnd(Separators);
        var i = trimmed.LastIndexOfAny(Separators);
        if (i < 0)
            return null;

        var parent = trimmed[..i];
        if (parent.Length == 0)
            return null; // "\a" の親はルート。遡らない
        if (IsUncServerRoot(parent))
            return null; // "\\nas" は共有名が無くディレクトリとして開けない
        if (parent.Length == 2 && parent[1] == ':')
            return parent + '\\'; // "I:" はドライブ相対(カレント)を指すので、ルート "I:\" に正規化する

        return parent;
    }

    /// <summary>
    /// <paramref name="path"/> の祖先を上へ辿り、最初に見つかった存在するディレクトリを返す。
    /// 見つからなければ null。<paramref name="path"/> 自身は候補にしない(存在しないファイルに対して
    /// 呼ぶための関数なので、必ず親から始める)。
    /// </summary>
    /// <param name="directoryExists">ディレクトリの存在判定(テストで差し替える)。</param>
    public static string? NearestExistingDirectory(string? path, Func<string, bool> directoryExists)
    {
        for (var current = Parent(path); current is not null; current = Parent(current))
        {
            if (directoryExists(current))
                return current;
        }

        return null;
    }

    // 先頭 2 文字が区切りで、その後に区切りが無い = "\\nas" 形式(共有名を伴わない UNC サーバ)。
    private static bool IsUncServerRoot(string path) =>
        path.Length > 2
        && IsSeparator(path[0])
        && IsSeparator(path[1])
        && path.IndexOfAny(Separators, 2) < 0;

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
