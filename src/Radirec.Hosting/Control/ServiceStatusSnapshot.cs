namespace Radirec.Hosting.Control;

/// <summary>
/// 常駐のライブ状態(状態取得コマンドの結果)。worker がスレッドセーフに publish し、
/// 読み手が直列チャネルを介さず直接読む(ADR-0010)。immutable record を volatile 参照で差し替える。
/// </summary>
public sealed record ServiceStatusSnapshot
{
    /// <summary>常駐の版。</summary>
    public required string Version { get; init; }

    /// <summary>常駐(worker)の起動時刻(UTC)。</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>現在の監視ルート。</summary>
    public required string RadioRoot { get; init; }

    /// <summary>
    /// 今読み込んでいる設定の放送局マスタのパス(解決済み)。設定ページの「ファイルを開く」(#143 / #152)が開く場所。
    /// 設定リロードの反映に成功したときだけ変わる(失敗なら旧構成のまま = 旧パスのまま)。
    /// </summary>
    public string StationsFile { get; init; } = string.Empty;

    /// <summary>今読み込んでいる設定の番組 alias のパス(解決済み)。<see cref="StationsFile"/> と同じ。</summary>
    public string ProgramsFile { get; init; } = string.Empty;

    /// <summary>FileSystemWatcher が有効か(false = ルート不在で定期スキャンのみ)。</summary>
    public required bool Watching { get; init; }

    /// <summary>
    /// 取り込みループが生きているか。トレイ常駐(ADR-0012)ではワーカーが異常終了しても
    /// トレイ(メッセージループ)は生存しうるため、「稼働中」と偽って表示しないための健全性フラグ。
    /// ExecuteAsync が抜けた(正常停止 or 異常終了)後は false。
    /// </summary>
    public bool Running { get; init; } = true;

    /// <summary>最後にスキャンを完了した時刻(UTC)。未走査なら null。</summary>
    public DateTimeOffset? LastScanCompletedUtc { get; init; }

    /// <summary>最後のスキャンのトリガ種別(検知/定期/起動時/スリープ復帰/手動)。未走査なら null。</summary>
    public string? LastScanTrigger { get; init; }

    /// <summary>最後のスキャンで取り込んだ件数。</summary>
    public int LastImported { get; init; }

    /// <summary>最後のスキャンで失敗した件数。</summary>
    public int LastFailed { get; init; }
}
