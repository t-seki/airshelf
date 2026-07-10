using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Radirec.Core.Ingestion;

namespace Radirec.Hosting;

/// <summary>
/// 取込要約シグナルを Windows イベントログ(Application / Source <c>Radirec</c>)へ
/// 専用 EventID で書き出す(ADR-0008)。ILogger には相乗りせず、logging 設定から独立させる。
/// 受け手(GUI/常駐)は <c>Source=Radirec &amp; EventID=1001</c> でフィルタ購読する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogIngestSummaryPublisher(string sourceName = "Radirec") : IIngestSummaryPublisher
{
    /// <summary>取込要約シグナルに予約した EventID(1000 番台を将来シグナル用に確保)。</summary>
    public const int IngestSummaryEventId = 1001;

    /// <summary>ペイロード JSON envelope のスキーマ版(前方進化用)。</summary>
    public const int SchemaVersion = 1;

    public Task PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default)
    {
        EventLog.WriteEntry(
            sourceName, BuildPayload(summary), EventLogEntryType.Information, IngestSummaryEventId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 受け手(GUI)が parse するワイヤ契約の JSON envelope を組み立てる(EventLog 非依存・純粋)。
    /// 放送日は ISO 8601 日付(<c>yyyy-MM-dd</c>)。
    /// </summary>
    public static string BuildPayload(IngestSummary summary)
    {
        var payload = new
        {
            schemaVersion = SchemaVersion,
            totalImported = summary.TotalImported,
            truncated = summary.Truncated,
            albums = summary.Albums.Select(a => new
            {
                station = a.Station,
                program = a.Program,
                episodes = a.Episodes.Select(e => new
                {
                    broadcastDate = e.BroadcastDate.ToString("yyyy-MM-dd"),
                    partIndex = e.PartIndex,
                }),
            }),
        };

        return JsonSerializer.Serialize(payload);
    }
}
