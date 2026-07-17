using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using Radirec.Gui.Core;

namespace Radirec.Gui;

/// <summary>
/// Application イベントログを <c>Source=Radirec</c> で購読し、取込要約(EventID 1001)と取込失敗(1002)を
/// parse して通知する(ADR-0009 / 0011)。2 フェーズ起動(軸A A2/A3):
/// フェーズ1(catch-up)= 保存 bookmark 起点で <see cref="EventLogReader"/> を同期的に読み切り、受け手停止中に
/// 溜まった分を成功・失敗それぞれ 1 件へマージして提示。フェーズ2(live)= その位置から
/// <see cref="EventLogWatcher"/>(<c>readExistingEvents:false</c>)でライブ購読。
/// 1001/1002 は EventID で振り分けるが、**チェックポイント(RecordId)はログ位置なので 1 個**が両シグナルを覆う。
/// bookmark の include/exclude 意味論が実機依存で読めないため、提示済み <c>RecordId</c> 以下は両フェーズで無視する
/// (単調ガード。二重計上の backstop)。bookmark 失効/破損は縮退(read-from-now)。EventLog は Windows 専用。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogSummarySubscriber : IDisposable
{
    private readonly string _queryText;
    private readonly SubscriptionCheckpoint? _initial;
    private readonly object _gate = new();

    private EventLogWatcher? _watcher;
    private long? _lastRecordId;

    /// <summary>解析に成功したライブ取込要約(1001)を通知する。</summary>
    public event Action<IngestSummaryPayload>? SummaryReceived;

    /// <summary>解析に成功したライブ取込失敗(1002)を通知する(ADR-0011)。</summary>
    public event Action<IngestFailurePayload>? FailureReceived;

    /// <summary>受け手停止中に溜まった取込要約をマージした不在中サマリ(起動時に最大 1 件)。</summary>
    public event Action<IngestSummaryPayload>? WhileAwaySummaryReceived;

    /// <summary>受け手停止中に溜まった取込失敗をマージした不在中サマリ(失敗版。起動時に最大 1 件)。</summary>
    public event Action<IngestFailurePayload>? WhileAwayFailureReceived;

    /// <summary>解析失敗・未対応 schemaVersion・購読エラー等の警告(トーストは出さずログ想定)。</summary>
    public event Action<string>? Warning;

    /// <summary>提示位置が進んだときに新チェックポイントを通知する(呼び手が永続化する)。</summary>
    public event Action<SubscriptionCheckpoint>? CheckpointAdvanced;

    public EventLogSummarySubscriber(SubscriptionCheckpoint? checkpoint = null)
    {
        // ソース名・EventID は受け手契約(Gui.Core)から。発行側 Hosting.RadirecEventLog との一致は契約テストで保証。
        // 1001(要約)と 1002(失敗)の両方を購読する。RecordId は EventID 非依存なのでチェックポイントは 1 個。
        _queryText = $"*[System[Provider[@Name='{NotificationContract.SourceName}'] "
            + $"and (EventID={NotificationContract.IngestSummaryEventId} "
            + $"or EventID={NotificationContract.IngestFailureEventId})]]";
        _initial = checkpoint;
        _lastRecordId = checkpoint?.LastRecordId;
    }

    /// <summary>不在中分の読み取り位置と、そこで拾った(=提示対象の)成功・失敗ペイロード。</summary>
    private readonly record struct DrainResult(
        EventBookmark? ResumeBookmark,
        long? MaxRecordId,
        IReadOnlyList<IngestSummaryPayload> Summaries,
        IReadOnlyList<IngestFailurePayload> Failures)
    {
        /// <summary>catch-up 無し(初回・縮退)。live は「今から先」で開始する。</summary>
        public static DrainResult ReadFromNow => new(null, null, [], []);
    }

    /// <summary>フェーズ1(catch-up 同期読み切り)→ 提示 → フェーズ2(live watcher)を起動する。</summary>
    public void Start()
    {
        DrainResult drain;
        try
        {
            drain = DrainWhileAway();
        }
        catch (Exception ex)
        {
            // catch-up は起動を止めてはならない。EventLog 例外以外(ACL 等の想定外)も含めて握り、
            // read-from-now へ縮退する(トレイが無痕跡で消える失敗モードを作らない)。
            Warning?.Invoke($"不在中サマリの読取に失敗(縮退します): {ex.Message}");
            drain = DrainResult.ReadFromNow;
        }

        EmitWhileAwayAndCheckpoint(drain);

        // resumeBookmark==null(初回/縮退/不在中なし) = 信頼できる購読位置が無い = 「今から先」。
        // 種として持ち込んだ _lastRecordId(旧チェックポイント)は無効。特にログクリアで RecordId が
        // 1 に戻ると、旧 baseline(例 5000)が全ライブ通知を IsNewer で潰し続けるため、必ずリセットする。
        if (drain.ResumeBookmark is null)
            ResetBaseline();

        StartLiveWatcher(drain.ResumeBookmark);
    }

    /// <summary>
    /// フェーズ1 の shim: 保存 bookmark 起点で既存レコードを同期的に読み切り、判定は純粋な
    /// <see cref="WhileAwayDrain.Plan"/>(Gui.Core・テスト済み)へ委譲する。ここは EventLog の前方向読み取りを
    /// <see cref="DrainRecord"/> 列へ落として EventID で parse するだけの薄い変換。
    /// </summary>
    private DrainResult DrainWhileAway()
    {
        if (_initial?.BookmarkXml is not { } bookmarkXml)
            return DrainResult.ReadFromNow; // 初回起動: catch-up 無しで live のみ。

        EventBookmark bookmark;
        try
        {
            bookmark = new EventBookmark(bookmarkXml);
        }
        catch (Exception ex)
        {
            // 破損 XML。縮退。
            Warning?.Invoke($"購読 bookmark が不正(縮退します): {ex.Message}");
            return DrainResult.ReadFromNow;
        }

        var query = new EventLogQuery("Application", PathType.LogName, _queryText);
        using var reader = new EventLogReader(query, bookmark);

        // baseline はドレイン中一定(Advance は提示成功後に 1 度だけ行う)。純粋リデューサへ渡す。
        var outcome = WhileAwayDrain.Plan(_lastRecordId, ReadDrainRecords(reader));

        // resume 位置は XML から復元する(persist と同じ round-trip。RecordId ガードが差異を backstop)。
        var resume = outcome.ResumeBookmarkXml is { } xml ? new EventBookmark(xml) : null;
        return new DrainResult(resume, outcome.MaxRecordId, outcome.Summaries, outcome.Failures);
    }

    /// <summary>EventLogReader を前方向に読み、各レコードを EventID で parse して <see cref="DrainRecord"/> へ落とす。</summary>
    private IEnumerable<DrainRecord> ReadDrainRecords(EventLogReader reader)
    {
        for (EventRecord? record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
        {
            using (record)
            {
                var (summary, failure) = ReadPayloads(record);
                yield return new DrainRecord(record.RecordId, record.Bookmark?.BookmarkXml, summary, failure);
            }
        }
    }

    /// <summary>
    /// 不在中サマリを提示し、通過した最終位置へチェックポイントを **1 回だけ** 確定する。成功 → 失敗の順に
    /// 出す(別シグナル = 別トースト。失敗を後に出して目に残す)。提示は **best-effort**:トースト表示が投げても
    /// (基盤不調・異常ペイロード等)チェックポイントは必ず進める。進めないと、live watcher は drained 位置から
    /// 始まるのに checkpoint が古いままで、次の live イベントの Advance が un-shown を追い越して**永久に消す**
    /// (取りこぼしより悪い silent loss)。あるいは異常ペイロードが毎起動で再 throw して以降の catch-up を
    /// 全ブロックする。取りこぼした分の元イベントは EventLog / gui.log(Warning)に残る — while-away トーストは
    /// 揮発シグナルの回収であって唯一の記録ではない。
    /// </summary>
    private void EmitWhileAwayAndCheckpoint(DrainResult drain)
    {
        TryEmit(() =>
        {
            if (drain.Summaries.Count > 0)
                WhileAwaySummaryReceived?.Invoke(WhileAwaySummary.Merge(drain.Summaries));
        }, "不在中サマリ(取込)");
        TryEmit(() =>
        {
            if (drain.Failures.Count > 0)
                WhileAwayFailureReceived?.Invoke(WhileAwayFailures.Merge(drain.Failures));
        }, "不在中サマリ(失敗)");

        if (drain.MaxRecordId is { } maxId)
            Advance(maxId, drain.ResumeBookmark);
    }

    /// <summary>不在中サマリの 1 トーストを best-effort で出す。表示失敗は握ってログに残す(checkpoint は進める)。</summary>
    private void TryEmit(Action emit, string label)
    {
        try
        {
            emit();
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"{label}の表示に失敗(スキップ。元イベントはログに残ります): {ex.Message}");
        }
    }

    /// <summary>フェーズ2: catch-up 済み位置から live watcher を start する。</summary>
    private void StartLiveWatcher(EventBookmark? resumeBookmark)
    {
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
            if (record.RecordId is not { } id || !IsNewer(id))
                return;

            var (summary, failure) = ReadPayloads(record);
            if (summary is { } s)
                SummaryReceived?.Invoke(s);
            else if (failure is { } f)
                FailureReceived?.Invoke(f);

            Advance(id, record.Bookmark);
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"通知処理で例外: {ex.Message}");
        }
    }

    /// <summary>
    /// レコードの生 JSON を EventID で振り分け parse する。1002 は失敗、それ以外(1001)は要約。解析失敗は
    /// Warning に流し両方 null を返す(位置は進めるが提示に含めない)。
    /// </summary>
    private (IngestSummaryPayload? Summary, IngestFailurePayload? Failure) ReadPayloads(EventRecord record)
    {
        if (!TryReadJson(record, out var json))
            return (null, null);

        if (record.Id == NotificationContract.IngestFailureEventId)
        {
            if (IngestFailurePayloadReader.TryParse(json!, out var failure, out var failError))
                return (null, failure);
            Warning?.Invoke(failError ?? "失敗シグナルの解析失敗");
            return (null, null);
        }

        if (IngestSummaryPayloadReader.TryParse(json!, out var summary, out var sumError))
            return (summary, null);
        Warning?.Invoke(sumError ?? "解析失敗");
        return (null, null);
    }

    /// <summary>レコードから JSON 本文を取り出す。カスタム EventID は FormatDescription が説明未登録を返しうるため Properties 優先。</summary>
    private bool TryReadJson(EventRecord record, out string? json)
    {
        json = record.Properties.FirstOrDefault()?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(json))
            json = record.FormatDescription();
        if (string.IsNullOrWhiteSpace(json))
        {
            Warning?.Invoke("空のイベントメッセージ");
            return false;
        }
        return true;
    }

    /// <summary>提示済み最大 RecordId(単調ガードの baseline)より新しいか。テスト実物(<see cref="SubscriptionCheckpoint.IsNewer(long?, long)"/>)を共有。</summary>
    private bool IsNewer(long recordId)
    {
        lock (_gate)
            return SubscriptionCheckpoint.IsNewer(_lastRecordId, recordId);
    }

    /// <summary>信頼できる購読位置が無い(read-from-now)場合に baseline を無効化する。</summary>
    private void ResetBaseline()
    {
        lock (_gate)
            _lastRecordId = null;
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
