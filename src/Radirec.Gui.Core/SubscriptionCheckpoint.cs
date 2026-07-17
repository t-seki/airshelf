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

    /// <summary>永続化文字列の分類(#56)。<see cref="Empty"/>=未記録(正常・無言)、<see cref="Corrupt"/>=
    /// 非空だが解析不能(呼び手が縮退を warn)、<see cref="Loaded"/>=復元成功。</summary>
    public enum CheckpointParse { Empty, Corrupt, Loaded }

    /// <summary>
    /// 永続化文字列を分類しつつ復元する。空/空白は <see cref="CheckpointParse.Empty"/>(正常な未記録=初回起動
    /// 相当)、非空だが JSON として解析できない/null に化けるものは <see cref="CheckpointParse.Corrupt"/>
    /// (呼び手が read-from-now への縮退を warn する)、成功は <see cref="CheckpointParse.Loaded"/>。
    /// 「破損を無言で握り潰さず痕跡を残す」ための判別点(TrayLog 流儀)。
    /// </summary>
    public static CheckpointParse Classify(string? json, out SubscriptionCheckpoint? checkpoint)
    {
        checkpoint = null;
        if (string.IsNullOrWhiteSpace(json))
            return CheckpointParse.Empty;
        try
        {
            checkpoint = JsonSerializer.Deserialize<SubscriptionCheckpoint>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return CheckpointParse.Corrupt;
        }
        // 非空なのに null(例: 文字列 "null")も破損扱い(未記録=空とは区別して warn 対象にする)。
        return checkpoint is null ? CheckpointParse.Corrupt : CheckpointParse.Loaded;
    }

    /// <summary>永続化文字列から復元する。破損・空なら null(呼び手は縮退=read-from-now)。空と破損を
    /// 区別したい呼び手は <see cref="Classify"/> を使う。</summary>
    public static SubscriptionCheckpoint? TryDeserialize(string? json) =>
        Classify(json, out var checkpoint) == CheckpointParse.Loaded ? checkpoint : null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
