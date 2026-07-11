using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using Radirec.Gui.Core;

namespace Radirec.Gui;

/// <summary>
/// Application イベントログを <c>Source=Radirec &amp; EventID=1001</c> で購読し、取込要約ペイロードを
/// parse して通知する(ADR-0009)。起動時は過去分を再生しない(read-from-now)。
/// EventLog は Windows 専用。実機で E2E 検証すること。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogSummarySubscriber : IDisposable
{
    // 発行側の契約(ADR-0008)。ソース名・EventID は Hosting.RadirecEventLog と一致させる。
    private const string SourceName = "Radirec";
    private const int IngestSummaryEventId = 1001;

    private readonly EventLogWatcher _watcher;

    /// <summary>解析に成功した取込要約を通知する。</summary>
    public event Action<IngestSummaryPayload>? SummaryReceived;

    /// <summary>解析失敗・未対応 schemaVersion 等の警告を通知する(トーストは出さずログ想定)。</summary>
    public event Action<string>? Warning;

    public EventLogSummarySubscriber()
    {
        var query = new EventLogQuery("Application", PathType.LogName,
            $"*[System[Provider[@Name='{SourceName}'] and EventID={IngestSummaryEventId}]]");

        // readExistingEvents:false = 今から先だけ。ログオン時に過去トーストが大量に出るのを避ける。
        _watcher = new EventLogWatcher(query, bookmark: null, readExistingEvents: false);
        _watcher.EventRecordWritten += OnEventWritten;
    }

    public void Start() => _watcher.Enabled = true;

    private void OnEventWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventException is not null)
        {
            Warning?.Invoke($"イベント購読エラー: {e.EventException.Message}");
            return;
        }

        if (e.EventRecord is null)
            return;

        using var record = e.EventRecord;

        // WriteEntry の生メッセージは挿入文字列 Properties[0] に入る。カスタム EventID は message
        // リソース未登録で FormatDescription() が「説明が見つかりません」を返しうるため Properties を優先。
        var json = record.Properties.FirstOrDefault()?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(json))
            json = record.FormatDescription();
        if (string.IsNullOrWhiteSpace(json))
        {
            Warning?.Invoke("空のイベントメッセージ");
            return;
        }

        if (IngestSummaryPayloadReader.TryParse(json, out var payload, out var error))
            SummaryReceived?.Invoke(payload!);
        else
            Warning?.Invoke(error ?? "解析失敗");
    }

    public void Dispose()
    {
        _watcher.Enabled = false;
        _watcher.EventRecordWritten -= OnEventWritten;
        _watcher.Dispose();
    }
}
