using Radirec.Core.Ledger;

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

    /// <summary>
    /// 取り込み(トレイ)のために、台帳を書き込み用に開く。フォルダがまだ無ければ作る(初めて使うとき。<see cref="SqliteLedger"/>)。
    /// </summary>
    /// <exception cref="LedgerUnreachableException">
    /// フォルダを作れない・開けない(NAS に届かない・権限が無い)。元の例外を内側に持つ。設定の反映の失敗が台帳のせいか、
    /// ほかの設定ファイル(programs.json など)のせいかを、理由の文字列ではなく型で見分けるため(#228)。
    /// </exception>
    public static SqliteLedger OpenWritable(string ledgerPath)
    {
        try
        {
            return new SqliteLedger(ledgerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LedgerUnreachableException(Path.GetDirectoryName(Path.GetFullPath(ledgerPath)) ?? ledgerPath, ex);
        }
    }
}

/// <summary>
/// 台帳の親フォルダに届かない(#216)。<see cref="IOException"/> なので、CLI では外部との接続の失敗として 5 になる。
/// 設定ファイルの読み込みの失敗(1)と分けて扱えるように、専用の型にしている(<c>ingest --dry-run</c>・<c>reapply</c>)。
/// </summary>
public sealed class LedgerUnreachableException : DirectoryNotFoundException
{
    public LedgerUnreachableException(string directory)
        : base($"台帳のあるフォルダに届きません: {directory}")
    {
    }

    /// <summary>台帳を書き込み用に開けなかった(<see cref="LedgerFile.OpenWritable"/>)。元の例外の文言も添える。</summary>
    public LedgerUnreachableException(string directory, Exception inner)
        : base($"台帳のあるフォルダに届きません: {directory}({inner.Message})", inner)
    {
    }
}
