using System.Text.Json;

namespace Radirec.Hosting.Control;

/// <summary>制御チャネルの要求 envelope(利用者 → サービス)。</summary>
public sealed record ControlRequest
{
    /// <summary>envelope スキーマ版。</summary>
    public int SchemaVersion { get; init; } = ControlContract.SchemaVersion;

    /// <summary>コマンド名(<see cref="ControlContract.Commands"/>)。</summary>
    public required string Command { get; init; }
}

/// <summary>制御チャネルの応答 envelope(サービス → 利用者)。</summary>
public sealed record ControlResponse
{
    /// <summary>envelope スキーマ版。</summary>
    public int SchemaVersion { get; init; } = ControlContract.SchemaVersion;

    /// <summary>コマンドを受理・処理できたか。</summary>
    public required bool Ok { get; init; }

    /// <summary>失敗時の理由(成功時 null)。</summary>
    public string? Error { get; init; }

    /// <summary>コマンド固有の結果ペイロード(status のスナップショット等。無い場合 null)。</summary>
    public JsonElement? Result { get; init; }

    public static ControlResponse Success(object? result = null) => new()
    {
        Ok = true,
        Result = result is null
            ? null
            : JsonSerializer.SerializeToElement(result, ControlContract.Json),
    };

    public static ControlResponse Failure(string error) => new() { Ok = false, Error = error };

    /// <summary><see cref="Result"/> を型付きで取り出す(CLI クライアント用。null なら default)。</summary>
    public T? ResultAs<T>() =>
        Result is { } element ? element.Deserialize<T>(ControlContract.Json) : default;
}
