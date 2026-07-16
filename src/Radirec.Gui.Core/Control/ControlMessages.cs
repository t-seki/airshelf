using System.Text.Json;

namespace Radirec.Gui.Core.Control;

/// <summary>
/// 制御チャネルの要求 envelope(利用者 GUI → サービス)の**送出側 DTO**。発行側 <c>ControlRequest</c> の
/// 型は共有せず、凍結 JSON 契約に自前で追従する(ドリフトは契約テストで検出)。
/// </summary>
public sealed record ControlRequest
{
    /// <summary>envelope スキーマ版。</summary>
    public int SchemaVersion { get; init; } = ControlContract.SchemaVersion;

    /// <summary>コマンド名(<see cref="ControlContract.Commands"/>)。</summary>
    public required string Command { get; init; }
}

/// <summary>
/// 制御チャネルの応答 envelope(サービス → 利用者 GUI)の**受け手側 DTO**。同一 schemaVersion 内の
/// 未知フィールドは無視する(前方互換)。
/// </summary>
public sealed record ControlResponse
{
    /// <summary>envelope スキーマ版。</summary>
    public int SchemaVersion { get; init; }

    /// <summary>コマンドを受理・処理できたか。</summary>
    public bool Ok { get; init; }

    /// <summary>失敗時の理由(成功時 null)。</summary>
    public string? Error { get; init; }

    /// <summary>コマンド固有の結果ペイロード(status のスナップショット等。無い場合 null)。</summary>
    public JsonElement? Result { get; init; }

    /// <summary><see cref="Result"/> を型付きで取り出す(null なら default)。</summary>
    public T? ResultAs<T>() =>
        Result is { } element ? element.Deserialize<T>(ControlContract.Json) : default;
}

/// <summary>
/// 状態取得コマンドの結果(サービスのライブ状態)の**受け手側 DTO**。省略されうるフィールドは既定値で
/// 受け、下流の NRE を防ぐ(発行側 <c>ServiceStatusSnapshot</c> の型は共有しない)。
/// </summary>
public sealed record ServiceStatusSnapshot
{
    /// <summary>サービスの版。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>サービス(worker)起動時刻(UTC)。</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>現在の監視ルート。</summary>
    public string RadioRoot { get; init; } = string.Empty;

    /// <summary>FileSystemWatcher が有効か(false = ルート不在で定期スキャンのみ)。</summary>
    public bool Watching { get; init; }

    /// <summary>最後にスキャンを完了した時刻(UTC)。未走査なら null。</summary>
    public DateTimeOffset? LastScanCompletedUtc { get; init; }

    /// <summary>最後のスキャンのトリガ種別。未走査なら null。</summary>
    public string? LastScanTrigger { get; init; }

    /// <summary>最後のスキャンで取り込んだ件数。</summary>
    public int LastImported { get; init; }

    /// <summary>最後のスキャンで失敗した件数。</summary>
    public int LastFailed { get; init; }
}
