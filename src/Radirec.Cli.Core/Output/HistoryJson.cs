using Radirec.Core.Ledger;

namespace Radirec.Cli.Core.Output;

/// <summary><c>history --json</c> の <c>result</c>。取込日時の新しい順。</summary>
/// <param name="Entries">絞り込んだ後の記録(<c>--since</c> で絞り、<c>--limit</c> 件まで)。</param>
public sealed record HistoryJson(IReadOnlyList<HistoryEntryJson> Entries);

/// <summary>
/// 取込履歴の 1 件。表の見た目に整形する前の値(#182)。
/// </summary>
/// <param name="BroadcastDate">放送日(<c>yyyy-MM-dd</c>)。</param>
/// <param name="BroadcastTime">放送時刻(<c>HH:mm:ss</c>)。時刻を持たない録音(radikool 等)は null。</param>
/// <param name="Part">同日の別放送を分ける番号(ADR-0004)。無ければ 0。</param>
/// <param name="ImportedAt">取込日時(ISO 8601。ローカルの時差つき)。</param>
/// <param name="OutputPath">出力先のフルパス。</param>
/// <param name="SourcePath">取込元のフルパス(取込後に消えている・移動していることがある)。</param>
public sealed record HistoryEntryJson(
    string Program,
    string Station,
    DateOnly BroadcastDate,
    TimeOnly? BroadcastTime,
    int Part,
    DateTimeOffset ImportedAt,
    string OutputPath,
    string SourcePath)
{
    public static HistoryEntryJson From(LedgerEntry entry) => new(
        entry.Program,
        entry.Station,
        entry.BroadcastDate,
        entry.BroadcastTime,
        entry.PartIndex,
        entry.ImportedAtUtc.ToLocalTime(),
        entry.OutputPath,
        entry.SourcePath);
}
