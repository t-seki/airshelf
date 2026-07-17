using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using Radirec.Gui.Core;

namespace Radirec.Gui;

/// <summary>
/// Application イベントログを <c>Source=Radirec &amp; EventID=1001</c> で購読し、取込要約ペイロードを
/// parse して通知する(ADR-0009)。2 フェーズ起動(軸A A2):
/// フェーズ1(catch-up)= 保存 bookmark 起点で <see cref="EventLogReader"/> を同期的に読み切り、
/// 受け手停止中に溜まった分を 1 件の**不在中サマリ**へマージ。フェーズ2(live)= その位置から
/// <see cref="EventLogWatcher"/>(<c>readExistingEvents:false</c>)でライブ購読。
/// bookmark の include/exclude 意味論が実機依存で読めないため、提示済み <c>RecordId</c> 以下は
/// 両フェーズで無視する(単調ガード。二重計上の backstop)。bookmark 失効/破損は縮退(read-from-now)。
/// EventLog は Windows 専用。実機で E2E 検証すること。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogSummarySubscriber : IDisposable
{
    private readonly string _queryText;
    private readonly SubscriptionCheckpoint? _initial;
    private readonly object _gate = new();

    private EventLogWatcher? _watcher;
    private long? _lastRecordId;

    /// <summary>解析に成功したライブ取込要約を通知する(1 走査 = 1 件)。</summary>
    public event Action<IngestSummaryPayload>? SummaryReceived;

    /// <summary>受け手停止中に溜まった取込をマージした不在中サマリ(起動時に最大 1 件)。</summary>
    public event Action<IngestSummaryPayload>? WhileAwaySummaryReceived;

    /// <summary>解析失敗・未対応 schemaVersion・購読エラー等の警告(トーストは出さずログ想定)。</summary>
    public event Action<string>? Warning;

    /// <summary>提示位置が進んだときに新チェックポイントを通知する(呼び手が永続化する)。</summary>
    public event Action<SubscriptionCheckpoint>? CheckpointAdvanced;

    public EventLogSummarySubscriber(SubscriptionCheckpoint? checkpoint = null)
    {
        // ソース名・EventID は受け手契約(Gui.Core)から。発行側 Hosting.RadirecEventLog との一致は契約テストで保証。
        _queryText = $"*[System[Provider[@Name='{NotificationContract.SourceName}'] "
            + $"and EventID={NotificationContract.IngestSummaryEventId}]]";
        _initial = checkpoint;
        _lastRecordId = checkpoint?.LastRecordId;
    }

    /// <summary>フェーズ1(catch-up 同期読み切り)→ フェーズ2(live watcher)を起動する。</summary>
    public void Start()
    {
        var resumeBookmark = DrainWhileAway();
        StartLiveWatcher(resumeBookmark);
    }

    /// <summary>
    /// フェーズ1: 保存 bookmark 起点で既存レコードを同期的に読み切り、不在中分をマージして通知する。
    /// 返り値は live watcher の起点にする bookmark(読み切った最後のレコード。無ければ null=先頭から)。
    /// bookmark 失効/破損時は warn して null を返す(縮退 = read-from-now)。
    /// </summary>
    private EventBookmark? DrainWhileAway()
    {
        if (_initial?.BookmarkXml is not { } bookmarkXml)
            return null; // 初回起動: catch-up 無しで live のみ。

        EventBookmark bookmark;
        try
        {
            bookmark = new EventBookmark(bookmarkXml);
        }
        catch (Exception ex)
        {
            // 破損 XML。縮退。
            Warning?.Invoke($"購読 bookmark が不正(縮退します): {ex.Message}");
            return null;
        }

        var collected = new List<IngestSummaryPayload>();
        EventBookmark? lastBookmark = null;

        try
        {
            var query = new EventLogQuery("Application", PathType.LogName, _queryText);
            using var reader = new EventLogReader(query, bookmark);

            for (EventRecord? record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    lastBookmark = record.Bookmark;
                    // 単調ガード: 提示済み ID 以下(bookmark が境界を再返却しうる)はスキップ。
                    if (record.RecordId is { } id && !IsNewer(id))
                        continue;

                    if (TryReadPayload(record, out var payload))
                        collected.Add(payload!);

                    if (record.RecordId is { } newId)
                        Advance(newId, record.Bookmark);
                }
            }
        }
        catch (EventLogNotFoundException ex)
        {
            Warning?.Invoke($"購読位置が失効(縮退します): {ex.Message}");
            return null;
        }
        catch (EventLogException ex)
        {
            Warning?.Invoke($"不在中サマリの読取に失敗(縮退します): {ex.Message}");
            return null;
        }

        if (collected.Count > 0)
            WhileAwaySummaryReceived?.Invoke(WhileAwaySummary.Merge(collected));

        return lastBookmark;
    }

    /// <summary>フェーズ2: catch-up 済み位置から live watcher を start する。</summary>
    private void StartLiveWatcher(EventBookmark? resumeBookmark)
    {
        // resumeBookmark==null = 信頼できる購読位置が無い(初回 or 縮退)= 「今から先」。このとき
        // 種として持ち込んだ _lastRecordId(旧チェックポイント)は無効。特にログクリアで RecordId が
        // 1 に戻ると、旧 baseline(例 5000)が全ライブ通知を IsNewer で潰し続けるため、必ずリセットする。
        // catch-up で何も拾っていない(縮退)ので二重提示の恐れはない。
        if (resumeBookmark is null)
            _lastRecordId = null;

        var query = new EventLogQuery("Application", PathType.LogName, _queryText);
        // resumeBookmark があればそこから先、無ければ(初回/縮退)従来どおり「今から先」。
        _watcher = new EventLogWatcher(query, resumeBookmark, readExistingEvents: false);
        _watcher.EventRecordWritten += OnEventWritten;
        _watcher.Enabled = true;
    }

    private void OnEventWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        // このハンドラは EventLogWatcher のスレッドプールスレッドで走る。ここから例外を漏らすと
        // 常駐トレイプロセスごと落ちるため、レコード読取・parse・購読者(トースト表示)まで含め
        // すべてを包んで握り、Warning に流す(通知の取りこぼしはあってもプロセスは生かす)。
        try
        {
            if (e.EventException is not null)
            {
                Warning?.Invoke($"イベント購読エラー: {e.EventException.Message}");
                return;
            }

            if (e.EventRecord is null)
                return;

            using var record = e.EventRecord;

            // 単調ガード: catch-up と live の境界でギャップ/再配信が起きても二重計上しない。
            if (record.RecordId is { } id && !IsNewer(id))
                return;

            if (TryReadPayload(record, out var payload))
                SummaryReceived?.Invoke(payload!);

            if (record.RecordId is { } newId)
                Advance(newId, record.Bookmark);
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"通知処理で例外: {ex.Message}");
        }
    }

    /// <summary>レコードから JSON を取り出し parse する。成功時 true。失敗は Warning に流して false。</summary>
    private bool TryReadPayload(EventRecord record, out IngestSummaryPayload? payload)
    {
        payload = null;

        // WriteEntry の生メッセージは挿入文字列 Properties[0] に入る。カスタム EventID は message
        // リソース未登録で FormatDescription() が「説明が見つかりません」を返しうるため Properties を優先。
        var json = record.Properties.FirstOrDefault()?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(json))
            json = record.FormatDescription();
        if (string.IsNullOrWhiteSpace(json))
        {
            Warning?.Invoke("空のイベントメッセージ");
            return false;
        }

        if (IngestSummaryPayloadReader.TryParse(json, out var parsed, out var error))
        {
            payload = parsed;
            return true;
        }

        Warning?.Invoke(error ?? "解析失敗");
        return false;
    }

    private bool IsNewer(long recordId)
    {
        lock (_gate)
            return _lastRecordId is not { } last || recordId > last;
    }

    /// <summary>提示位置を進め、チェックポイントを通知する(呼び手が永続化)。</summary>
    private void Advance(long recordId, EventBookmark? bookmark)
    {
        SubscriptionCheckpoint checkpoint;
        lock (_gate)
        {
            if (_lastRecordId is { } last && recordId <= last)
                return; // 後退しない(順不同配信への保険)。
            _lastRecordId = recordId;
            checkpoint = new SubscriptionCheckpoint
            {
                BookmarkXml = bookmark?.BookmarkXml,
                LastRecordId = recordId,
            };
        }
        CheckpointAdvanced?.Invoke(checkpoint);
    }

    public void Dispose()
    {
        if (_watcher is { } w)
        {
            w.Enabled = false;
            w.EventRecordWritten -= OnEventWritten;
            w.Dispose();
        }
    }
}
