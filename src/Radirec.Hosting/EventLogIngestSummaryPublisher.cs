using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Radirec.Core.Ingestion;

namespace Radirec.Hosting;

/// <summary>
/// 取込要約シグナルを Windows イベントログ(Application / Source <see cref="RadirecEventLog.SourceName"/>)へ
/// 専用 EventID で書き出す(ADR-0008)。ILogger には相乗りせず、logging 設定から独立させる。
/// 受け手(GUI/常駐)は <c>Source=Radirec &amp; EventID=1001</c> でフィルタ購読する。
/// </summary>
public sealed class EventLogIngestSummaryPublisher(string sourceName = RadirecEventLog.SourceName)
    : IIngestSummaryPublisher
{
    // EventLog は Windows 専用。呼び出しは CompositionRoot の OperatingSystem.IsWindows() ガードで保護。
    [SupportedOSPlatform("windows")]
    public Task PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default)
    {
        EventLog.WriteEntry(
            sourceName, BuildPayload(summary),
            EventLogEntryType.Information, RadirecEventLog.IngestSummaryEventId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 受け手(GUI)が parse するワイヤ契約の JSON envelope を組み立てる(EventLog 非依存・純粋)。
    /// 放送日は ISO 8601 日付(<c>yyyy-MM-dd</c>)、放送時刻は <c>HH:mm</c>(取得不能なら null)。
    /// </summary>
    public static string BuildPayload(IngestSummary summary)
    {
        var payload = new
        {
            schemaVersion = RadirecEventLog.SummarySchemaVersion,
            totalImported = summary.TotalImported,
            truncated = summary.Truncated,
            albums = summary.Albums.Select(a => new
            {
                station = a.Station,
                program = a.Program,
                episodes = a.Episodes.Select(e => new
                {
                    // ワイヤ契約なのでカルチャ非依存で書く("HH:mm" の ':' は time separator に
                    // 置換されうるため InvariantCulture 必須)。
                    broadcastDate = e.BroadcastDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    broadcastTime = e.BroadcastTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
                    partIndex = e.PartIndex,
                }),
            }),
        };

        return JsonSerializer.Serialize(payload, RadirecEventLog.PayloadJsonOptions);
    }
}
