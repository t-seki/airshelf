namespace Airshelf.Hosting;

/// <summary>環境設定(airshelf.json)。個人固有値はここに集約しコードにハードコードしない。</summary>
public sealed class AirshelfConfig
{
    /// <summary>らじれこ出力ルート(例 <c>I:\radio\tmp</c>)。</summary>
    public string RadioRoot { get; set; } = string.Empty;

    /// <summary>配信先ルート(ローカル or UNC。例 <c>\\host\share\media\radio</c>)。</summary>
    public string OutputRoot { get; set; } = string.Empty;

    /// <summary>台帳 DB パス(ローカルディスク。SMB には置かない)。</summary>
    public string LedgerPath { get; set; } = "ledger.db";

    /// <summary>安定判定の待機秒数。</summary>
    public int StabilitySeconds { get; set; } = 15;

    /// <summary>既定ジャンル(programs.json の Defaults.Genre が優先)。</summary>
    public string Genre { get; set; } = "ラジオ";

    /// <summary>ジャンク録音を除外するファイル名部分一致パターン。</summary>
    public List<string> SkipPatterns { get; set; } = [];

    /// <summary>剥がしてよい録音種別マーカーの許可リスト。</summary>
    public List<string> KnownRecordingKinds { get; set; } = ["TimeFree"];

    /// <summary>放送局マスタのパス。</summary>
    public string StationsFile { get; set; } = "stations.json";

    /// <summary>番組 alias のパス。</summary>
    public string ProgramsFile { get; set; } = "programs.json";

    /// <summary>Jellyfin のルート URL(例 <c>https://jellyfin.example.net</c>)。未設定なら取込後リフレッシュしない。</summary>
    public string JellyfinUrl { get; set; } = string.Empty;

    /// <summary>Jellyfin API キー(ダッシュボード → API キーで発行)。</summary>
    public string JellyfinApiKey { get; set; } = string.Empty;

    /// <summary>常駐の定期(安全網)スキャン間隔・秒。</summary>
    public int ScanIntervalSeconds { get; set; } = 120;

    /// <summary>検知後スキャンまでのデバウンス秒(書き込み中の連続イベントを束ねる)。</summary>
    public int WatchDebounceSeconds { get; set; } = 10;

    /// <summary>
    /// **旧設定**(#152・ADR-0019)。新規取込のトーストを出すかは、管理画面の設定ページで切り替え、管理画面用の設定ファイル
    /// (<c>gui-settings.json</c>)に持つ。ここは、その設定ファイルにまだ値が無いときの**初期値としてだけ**読む
    /// (<c>false</c> なら新規取込のトーストを止めた状態で始める)。取込失敗のトーストと失敗のストアは、この値に関係なく動く。
    /// 既存の airshelf.json を読めなくしないため、キーは残す(未知のキーは読み込みで失敗させるので)。既定 true。
    /// </summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>
    /// アプリ内自己更新(Velopack)で prerelease(RC)も対象にするか。既定 false(安定版のみ)。
    /// RC の更新フロー検証時のみ true にする(S6 / ADR-0013)。
    /// </summary>
    public bool UpdatePrerelease { get; set; }

    /// <summary>
    /// 更新フィード(GitHub Releases)を読むためのトークン。**private repo では必須**
    /// (contents:read の fine-grained PAT)。空なら public 扱いで認証なし。バイナリには埋め込まず
    /// ここ(ローカル config)にだけ置く(ADR-0013)。
    /// </summary>
    public string UpdateToken { get; set; } = string.Empty;
}
