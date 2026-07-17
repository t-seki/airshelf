namespace Radirec.Gui.Core;

/// <summary>1 件のドレイン対象レコードを、判定に要る最小フィールドへ落としたもの(軸A A2)。</summary>
/// <param name="RecordId">EventLog の <c>RecordId</c>(取得不能なら null → 判定対象外)。</param>
/// <param name="BookmarkXml">このレコードの <c>EventBookmark.BookmarkXml</c>(live 再開位置に使う)。</param>
/// <param name="Parsed">解析に成功した取込要約(失敗/未対応なら null → 提示に含めない)。</param>
public readonly record struct DrainRecord(long? RecordId, string? BookmarkXml, IngestSummaryPayload? Parsed);

/// <summary>ドレイン計画の結果(軸A A2)。</summary>
/// <param name="Collected">不在中サマリへマージする(=提示対象の)ペイロード。</param>
/// <param name="ResumeBookmarkXml">live watcher の再開位置。null なら「今から先」(縮退)。</param>
/// <param name="MaxRecordId">通過した最大 RecordId(チェックポイント確定に使う)。null なら通過ゼロ。</param>
public readonly record struct DrainOutcome(
    IReadOnlyList<IngestSummaryPayload> Collected,
    string? ResumeBookmarkXml,
    long? MaxRecordId);

/// <summary>
/// 不在中ドレインの**純粋な判定ロジック**(軸A A2)。EventLog の前方向読み取りを抽象ストリーム
/// (<see cref="DrainRecord"/> 列)に落とし、Windows 非依存で単体テストできるようにする。ここが本機能の
/// 要の正しさ — 単調ガード / 通過分だけの位置前進 / 全件スキップ時の縮退 — を担う(過去 2 度この判定で
/// 別々の subtle bug を出したため、抽出してテストで固定する)。
///
/// 規則: baseline(提示済み最大 RecordId)より新しいレコードだけを「通過」とし、その分だけ resume 位置と
/// max を進める。baseline 以下(bookmark の境界再返却 / ログクリアでの ID 巻き戻り)は完全にスキップする。
/// 全件スキップなら <see cref="DrainOutcome.ResumeBookmarkXml"/> は null のままとなり、呼び手は
/// read-from-now へ縮退できる(旧 baseline がライブ通知を永久抑制する事故を防ぐ)。
/// </summary>
public static class WhileAwayDrain
{
    /// <summary>ドレインを計画する。<paramref name="baseline"/> は起動時点の提示済み最大 RecordId(null=未記録)。</summary>
    public static DrainOutcome Plan(long? baseline, IEnumerable<DrainRecord> records)
    {
        var collected = new List<IngestSummaryPayload>();
        string? resumeBookmarkXml = null;
        long? maxRecordId = null;

        foreach (var record in records)
        {
            // 単調ガード。位置(resume / max)は通過した分だけ進める — スキップしたレコードの bookmark で
            // 進めると、全件スキップ時に resume が非 null になり縮退判定が壊れる(過去バグ)。
            if (record.RecordId is not { } id || !SubscriptionCheckpoint.IsNewer(baseline, id))
                continue;

            resumeBookmarkXml = record.BookmarkXml;
            maxRecordId = id;

            if (record.Parsed is { } payload)
                collected.Add(payload);
        }

        return new DrainOutcome(collected, resumeBookmarkXml, maxRecordId);
    }
}
