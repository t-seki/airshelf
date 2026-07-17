namespace Radirec.Gui.Core;

/// <summary>
/// 不在中サマリ(While-Away Summary)の集約ロジック(表示専用・純粋。ADR-0009 拡張 / 軸A A2)。
/// 受け手が停止していた間に発行された複数の取込要約シグナルを、1 件の <see cref="IngestSummaryPayload"/> へ
/// マージする。マージ結果は既存 <see cref="IngestToastFormatter.BuildWhileAwayTexts"/> でそのままトースト化できる。
///
/// マージ規則:
/// - <see cref="IngestSummaryPayload.TotalImported"/> は各シグナルの単純合計。
/// - 同一番組(<see cref="IngestSummaryAlbumPayload.Station"/> + <see cref="IngestSummaryAlbumPayload.Program"/>)は
///   1 アルバムへ統合し、放送回を連結する(出現順を保つ)。
/// - <see cref="IngestSummaryPayload.Truncated"/> はいずれかが true なら true(OR)。
/// マージは可換ではなく**入力順に依存**する(アルバム/放送回の並びは最初の出現順)。呼び手は EventLog の
/// 時系列(古い順)で渡すこと。
/// </summary>
public static class WhileAwaySummary
{
    /// <summary>複数シグナルを 1 件へマージする。空入力なら <see cref="TotalImported"/>=0 の空サマリ。</summary>
    public static IngestSummaryPayload Merge(IReadOnlyList<IngestSummaryPayload> signals)
    {
        var total = 0;
        var truncated = false;

        // (Station, Program) → (先頭アルバム, 放送回の累積リスト)。放送回は可変リストに追記していき、
        // 最後に 1 度だけ確定する(同一番組が多数のシグナルに跨っても放送回コピーが二乗にならない)。
        // 出現順を保つため挿入順を覚える。
        var byProgram =
            new Dictionary<(string, string), (IngestSummaryAlbumPayload Header, List<IngestSummaryEpisodePayload> Episodes)>();
        var order = new List<(string, string)>();

        foreach (var signal in signals)
        {
            total += signal.TotalImported;
            truncated |= signal.Truncated;

            foreach (var album in signal.Albums)
            {
                var key = (album.Station, album.Program);
                if (byProgram.TryGetValue(key, out var acc))
                {
                    acc.Episodes.AddRange(album.Episodes); // 同一 List 参照へ追記。
                }
                else
                {
                    byProgram[key] = (album, [.. album.Episodes]);
                    order.Add(key);
                }
            }
        }

        return new IngestSummaryPayload
        {
            SchemaVersion = NotificationContract.SupportedSchemaVersion,
            TotalImported = total,
            Truncated = truncated,
            Albums = [.. order.Select(k => byProgram[k].Header with { Episodes = byProgram[k].Episodes })],
        };
    }
}
