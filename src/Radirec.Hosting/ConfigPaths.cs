namespace Radirec.Hosting;

/// <summary>
/// 設定ファイルの発見と、設定内相対パスの解決基準を一元化する(ADR-0006)。
/// バイナリ(<c>C:\Program Files\radirec\</c>)とデータ(<c>%ProgramData%\radirec\</c>)を分離したため、
/// 設定は ProgramData 側を既定発見し、設定内の相対パスは**設定ファイル自身のディレクトリ基準**で解決する。
/// </summary>
public static class ConfigPaths
{
    /// <summary>データルート = <c>%ProgramData%\radirec\</c>(Windows。他 OS では CommonApplicationData 配下)。</summary>
    public static string DataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "radirec");

    /// <summary>引数省略時に探す既定の設定ファイル(<c>%ProgramData%\radirec\radirec.json</c>)。</summary>
    public static string DefaultConfigPath => Path.Combine(DataRoot, "radirec.json");

    /// <summary>明示パスがあればそれを、無ければ <see cref="DefaultConfigPath"/> を返す。</summary>
    public static string Resolve(string? explicitPath) =>
        string.IsNullOrWhiteSpace(explicitPath) ? DefaultConfigPath : explicitPath;

    /// <summary>
    /// 相対パスを <paramref name="baseDir"/> 基準の絶対パスへ寄せる。
    /// 絶対パス(<see cref="Path.IsPathRooted(string)"/>)は素通り = 既存の絶対パス設定は挙動不変(後方互換)。
    /// </summary>
    /// <remarks>
    /// 絶対判定は実行 OS の規則に従う。本番は Windows 実行のため <c>C:\...</c> は絶対として素通りする。
    /// </remarks>
    public static string Rebase(string baseDir, string path) =>
        string.IsNullOrEmpty(path) || Path.IsPathRooted(path)
            ? path
            : Path.Combine(baseDir, path);

    /// <summary>
    /// 設定内の相対パス(監視ルート・出力先・台帳・放送局マスタ・番組 alias)を、
    /// 設定ファイルのあるディレクトリ基準へ寄せる。CWD(サービスでは System32)基準の解決を排する。
    /// 本番はいずれも絶対パス(UNC 含む)のため実質 no-op = 後方互換。
    /// </summary>
    public static void RebaseAgainst(RadirecConfig config, string configFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(configFilePath))
                  ?? Directory.GetCurrentDirectory();
        config.RadioRoot = Rebase(dir, config.RadioRoot);
        config.OutputRoot = Rebase(dir, config.OutputRoot);
        config.LedgerPath = Rebase(dir, config.LedgerPath);
        config.StationsFile = Rebase(dir, config.StationsFile);
        config.ProgramsFile = Rebase(dir, config.ProgramsFile);
    }
}
