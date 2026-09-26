namespace Radirec.Hosting;

/// <summary>
/// 台帳を読む側(取込履歴・要対応。管理画面と CLI)が、「台帳がまだ無い(取込ゼロ)」と「台帳に届かない」を見分ける(#216)。
/// <see cref="File.Exists(string)"/> は、届かない UNC・権限の無い場所でも例外を出さずに false を返すので、それだけでは
/// NAS が落ちているときに「何も無い」と答えてしまう。台帳の親フォルダが見えるかで見分ける。
/// </summary>
public static class LedgerFile
{
    /// <summary>
    /// 台帳のファイルがあれば true。親フォルダはあるがファイルがまだ無い(初めて使うとき)なら false。
    /// </summary>
    /// <exception cref="LedgerUnreachableException">
    /// 台帳の親フォルダに届かない(存在しない・読めない・NAS に届かない)。<see cref="IOException"/> なので、CLI では 5 になる。
    /// </exception>
    public static bool Exists(string ledgerPath)
    {
        // 親フォルダを先に見る。届かない NAS では 1 回の問い合わせごとに SMB の時間切れ(約 20 秒)を待ちうるので、
        // File.Exists → Directory.Exists の順だと 2 回待つ。先に見て届かなければその場で投げ、問い合わせを 1 回にする。
        var directory = Path.GetDirectoryName(Path.GetFullPath(ledgerPath));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            throw new LedgerUnreachableException(directory);
        return File.Exists(ledgerPath);
    }
}

/// <summary>
/// 台帳の親フォルダに届かない(#216)。<see cref="IOException"/> なので、CLI では外部との接続の失敗として 5 になる。
/// 設定ファイルの読み込みの失敗(1)と分けて扱えるように、専用の型にしている(<c>ingest --dry-run</c>)。
/// </summary>
public sealed class LedgerUnreachableException(string directory)
    : DirectoryNotFoundException($"台帳のあるフォルダに届きません: {directory}");
