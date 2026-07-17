namespace Radirec.Gui.Core;

/// <summary>
/// 1 件のドレイン対象レコードを、判定に要る最小フィールドへ落としたもの(軸A A2 / A3)。EventID で
/// 成功要約(1001)か失敗(1002)かが決まり、<see cref="Summary"/> か <see cref="Failure"/> の一方だけが
/// 入る(解析失敗なら両方 null。位置は進めるが提示に含めない)。
/// </summary>
/// <param name="RecordId">EventLog の <c>RecordId</c>(取得不能なら null → 判定対象外)。</param>
/// <param name="BookmarkXml">このレコードの <c>EventBookmark.BookmarkXml</c>(live 再開位置に使う)。</param>
/// <param name="Summary">1001 の解析結果(成功要約)。1002 や解析失敗では null。</param>
/// <param name="Failure">1002 の解析結果(取込失敗)。1001 や解析失敗では null。</param>
public readonly record struct DrainRecord(
    long? RecordId, string? BookmarkXml, IngestSummaryPayload? Summary, IngestFailurePayload? Failure);

/// <summary>ドレイン計画の結果(軸A A2 / A3)。成功・失敗を別コレクションで返す(別トーストになる)。</summary>
/// <param name="Summaries">提示対象の成功要約(1001)。</param>
/// <param name="Failures">提示対象の取込失敗(1002)。</param>
/// <param name="ResumeBookmarkXml">live watcher の再開位置。null なら「今から先」(縮退)。</param>
/// <param name="MaxRecordId">通過した最大 RecordId(チェックポイント確定に使う)。null なら通過ゼロ。</param>
public readonly record struct DrainOutcome(
    IReadOnlyList<IngestSummaryPayload> Summaries,
    IReadOnlyList<IngestFailurePayload> Failures,
    string? ResumeBookmarkXml,
    long? MaxRecordId);

/// <summary>
/// 不在中ドレインの**純粋な判定ロジック**(軸A A2 / A3)。EventLog の前方向読み取りを抽象ストリーム
/// (<see cref="DrainRecord"/> 列)に落とし、Windows 非依存で単体テストできるようにする。ここが本機能の
/// 要の正しさ — 単調ガード / 通過分だけの位置前進 / 全件スキップ時の縮退 — を担う(過去 2 度この判定で
/// 別々の subtle bug を出したため、抽出してテストで固定する)。1001/1002 は EventID で振り分け、成功・失敗を
/// 別コレクションへ集約する(**単一チェックポイントで両シグナルを覆う**。ADR-0011)。
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
        var summaries = new List<IngestSummaryPayload>();
        var failures = new List<IngestFailurePayload>();
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

            // 位置は「通過した=新規」レコードで進める。解析できなかったレコード(未対応 schemaVersion 等)も
            // 位置を進めて**スキップ**する — 進めないと毎起動で再 throw/再解析して以降の catch-up を全ブロック
            // する(poison)。発行側と受け手は同一バイナリで配布され schemaVersion が lockstep なので、
            // 解析不能は実運用で起きない前提。「skip-incompatible > poison-block」の割り切り(ADR-0011)。
            if (record.Summary is { } summary)
                summaries.Add(summary);
            else if (record.Failure is { } failure)
                failures.Add(failure);
        }

        return new DrainOutcome(summaries, failures, resumeBookmarkXml, maxRecordId);
    }
}
