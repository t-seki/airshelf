using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Radirec.Core.Ingestion;

namespace Radirec.Hosting;

/// <summary>
/// 取込失敗シグナルを Windows イベントログ(Application / Source <see cref="RadirecEventLog.SourceName"/>)へ
/// 専用 EventID 1002 で書き出す(ADR-0011)。成功要約(1001)と同じソース・別 EventID。受け手は
/// <c>Source=Radirec &amp; EventID=1002</c> で購読し、失敗通知(トースト)として表示する。
/// </summary>
public sealed class EventLogIngestFailurePublisher(string sourceName = RadirecEventLog.SourceName)
    : IIngestFailurePublisher
{
    // EventLog は Windows 専用。呼び出しは CompositionRoot の OperatingSystem.IsWindows() ガードで保護。
    [SupportedOSPlatform("windows")]
    public Task PublishAsync(IngestFailureSignal signal, CancellationToken cancellationToken = default)
    {
        // 失敗は Warning レベルで書く(受け手は EventID で判別するが、Event Viewer 上でも区別できる)。
        EventLog.WriteEntry(
            sourceName, BuildPayload(signal),
            EventLogEntryType.Warning, RadirecEventLog.IngestFailureEventId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 受け手(GUI)が parse するワイヤ契約の JSON envelope を組み立てる(EventLog 非依存・純粋)。
    /// <c>files[]</c> はファイル名のみ(フルパスは晒さない)。1001 と同じ count + 上限付きリスト + truncated の流儀。
    /// </summary>
    public static string BuildPayload(IngestFailureSignal signal)
    {
        var payload = new
        {
            schemaVersion = RadirecEventLog.FailureSchemaVersion,
            failedCount = signal.FailedCount,
            truncated = signal.Truncated,
            files = signal.Files.Select(f => new
            {
                name = f.Name,
                reason = f.Reason,
            }),
        };

        return JsonSerializer.Serialize(payload, RadirecEventLog.PayloadJsonOptions);
    }
}
