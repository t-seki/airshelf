namespace Airshelf.Hosting;

/// <summary>
/// 設定ファイルの発見と、設定内相対パスの解決基準を一元化する(ADR-0006 / 0012 / 0013)。
/// トレイ常駐 per-user 移行(ADR-0012/0013)で、データは per-user の <c>%LOCALAPPDATA%\RadirecData\</c> に置く
/// (Velopack の versioned app ディレクトリの外・非 admin・非 roaming。SQLite 台帳は roaming 不可)。
/// フォルダ名を <c>Radirec</c> にしてはならない理由は <see cref="DataRoot"/> を参照。
/// 設定は DataRoot 側を既定発見し、設定内の相対パスは**設定ファイル自身のディレクトリ基準**で解決する。
/// </summary>
public static class ConfigPaths
{
    /// <summary>
    /// データルート = <c>%LOCALAPPDATA%\RadirecData\</c>(Windows。他 OS では LocalApplicationData 配下)。
    /// 台帳・設定・マスタを置く。Local(非 roaming)= SQLite 台帳がマシン間同期で壊れないため。
    /// **フォルダ名は Velopack の packId 'Radirec'(インストール先 <c>%LOCALAPPDATA%\Radirec</c>)と
    /// 意図的に違える** — Windows は case-insensitive で <c>radirec</c>≒<c>Radirec</c> になり、同一だと
    /// 台帳が Velopack のアンインストールで消される(=全再取込)。ADR-0013 の衝突回避。
    /// </summary>
    public static string DataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RadirecData");

    /// <summary>既定の設定ファイル。トレイと CLI はどちらもこれを使う(CLI の <c>--config</c> は ADR-0020 で削った)。</summary>
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
    /// 設定ファイルのあるディレクトリ基準へ寄せる。**プロセスの CWD 基準の解決を排する**
    /// (CWD は起動のされ方に依存し、設定の解決先が呼び出し元次第で変わってしまうため)。
    /// 本番はいずれも絶対パス(UNC 含む)のため実質 no-op = 後方互換。
    /// </summary>
    public static void RebaseAgainst(AirshelfConfig config, string configFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(configFilePath))
                  ?? Directory.GetCurrentDirectory();
        config.RadioRoot = Rebase(dir, config.RadioRoot);
        config.OutputRoot = Rebase(dir, config.OutputRoot);
        config.LedgerPath = Rebase(dir, config.LedgerPath);
        config.StationsFile = Rebase(dir, config.StationsFile);
        config.ProgramsFile = Rebase(dir, config.ProgramsFile);
    }

    /// <summary>
    /// 設定ファイル <paramref name="file"/> の場所を、今読み込んでいる設定から求める(設定ページの「ファイルを開く」#143 / #152)。
    /// <c>radirec.json</c> は <paramref name="configFilePath"/> そのもの、<c>stations.json</c> / <c>programs.json</c> は
    /// <see cref="RebaseAgainst"/> 済みの <paramref name="stationsFile"/> / <paramref name="programsFile"/>
    /// (<c>StationsFile</c> / <c>ProgramsFile</c> で既定と違う場所を指していればその場所)。
    /// </summary>
    /// <returns>
    /// 場所を決められないときは null。設定を読めていない(パスが null)か、パスが空(<see cref="ConfigLoader.LoadOptional{T}"/>
    /// が何も読まない = そのファイルを使っていない)とき。
    /// </returns>
    public static string? Locate(ConfigFile file, string configFilePath, string? stationsFile, string? programsFile)
    {
        var path = file switch
        {
            ConfigFile.Airshelf => configFilePath,
            ConfigFile.Stations => stationsFile,
            ConfigFile.Programs => programsFile,
            _ => throw new ArgumentOutOfRangeException(nameof(file), file, null),
        };
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }
}

/// <summary>利用者が編集する設定ファイルの種類(見本から作るときの種類と、開く場所の計算に使う)。</summary>
public enum ConfigFile
{
    /// <summary>環境設定 <c>radirec.json</c>。</summary>
    Airshelf,

    /// <summary>番組 alias・統合設定 <c>programs.json</c>(<c>ProgramsFile</c>)。</summary>
    Programs,

    /// <summary>放送局マスタ <c>stations.json</c>(<c>StationsFile</c>)。</summary>
    Stations,
}
