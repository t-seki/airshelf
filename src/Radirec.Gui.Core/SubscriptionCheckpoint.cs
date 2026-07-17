using System.Text.Json;

namespace Radirec.Gui.Core;

/// <summary>
/// 購読チェックポイント(不在中サマリの起点。軸A A2)。最後に**提示済み**の EventLog 位置を表す。
/// <see cref="BookmarkXml"/> は次回起動時の <c>EventLogReader</c> の位置決め(効率)に、
/// <see cref="LastRecordId"/> は正しさの backstop(単調ガード)に使う — bookmark の include/exclude 意味論が
/// 実機依存で読めないため、<c>RecordId &lt;= LastRecordId</c> のレコードは両フェーズで無視する。
/// </summary>
public sealed record SubscriptionCheckpoint
{
    /// <summary><c>EventBookmark.BookmarkXml</c>。初回起動・未記録なら null。</summary>
    public string? BookmarkXml { get; init; }

    /// <summary>提示済みの最大 <c>EventRecord.RecordId</c>。初回起動・未記録なら null。</summary>
    public long? LastRecordId { get; init; }

    /// <summary>この位置より後(<c>RecordId &gt; LastRecordId</c>)を新規とみなす。未記録なら常に新規。</summary>
    public bool IsNewer(long recordId) => IsNewer(LastRecordId, recordId);

    /// <summary>
    /// 単調ガードの本体(<paramref name="lastRecordId"/> 未記録なら常に新規)。ランタイム(購読側)と
    /// テストの双方がこの 1 実装を呼ぶ — 比較ロジックを二重持ちにしてテストが実物を検証しない事態を防ぐ。
    /// </summary>
    public static bool IsNewer(long? lastRecordId, long recordId) =>
        lastRecordId is not { } last || recordId > last;

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>永続化文字列から復元する。破損・空なら null(呼び手は縮退=read-from-now)。</summary>
    public static SubscriptionCheckpoint? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<SubscriptionCheckpoint>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
