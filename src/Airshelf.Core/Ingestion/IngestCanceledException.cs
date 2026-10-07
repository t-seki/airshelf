namespace Airshelf.Core.Ingestion;

/// <summary>
/// 走査が取り消し(常駐の停止要求など)で途中で打ち切られた。打ち切られるまでに処理した明細(<see cref="Items"/>)を持つ(#218)。
/// <see cref="OperationCanceledException"/> の派生なので、取り消しを捕まえる今までの作り(停止要求はループへ伝える)はそのまま効く。
/// 取り込む走査(dry-run でない)なら、打ち切られるまでの取り込みは台帳と出力先に残っている(残りは次の走査が拾う)。
/// dry-run(<c>ingest --dry-run</c>)はもともと何も書かないので、明細は予定だけを表す。
/// </summary>
public sealed class IngestCanceledException(
    IReadOnlyList<IngestItemResult> items, OperationCanceledException inner, CancellationToken cancellationToken)
    : OperationCanceledException("走査は取り消しで打ち切られました", inner, cancellationToken)
{
    /// <summary>打ち切られるまでに処理したファイルの明細(処理の途中だったファイルは含まない)。</summary>
    public IReadOnlyList<IngestItemResult> Items { get; } = items;

    public int Count(IngestOutcome outcome) => Items.Count(i => i.Outcome == outcome);
}
