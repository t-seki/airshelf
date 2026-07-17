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

    /// <summary>不在中分の読み取り位置と、そこで拾った(=提示対象の)ペイロード。</summary>
    private readonly record struct DrainResult(
        EventBookmark? ResumeBookmark,
        long? MaxRecordId,
        IReadOnlyList<IngestSummaryPayload> Collected)
    {
        /// <summary>catch-up 無し(初回・縮退)。live は「今から先」で開始する。</summary>
        public static DrainResult ReadFromNow => new(null, null, []);
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
        // resumeBookmark が null のときは catch-up で何も通過していない(= 進めていない)ので安全。
        if (drain.ResumeBookmark is null)
            ResetBaseline();

        StartLiveWatcher(drain.ResumeBookmark);
    }

    /// <summary>
    /// フェーズ1 の shim: 保存 bookmark 起点で既存レコードを同期的に読み切り、判定は純粋な
    /// <see cref="WhileAwayDrain.Plan"/>(Gui.Core・テスト済み)へ委譲する。ここは EventLog の前方向
    /// 読み取りを <see cref="DrainRecord"/> 列へ落として parse する薄い変換だけを持つ。単調ガード /
    /// 位置前進 / 縮退の判定はすべてリデューサ側。
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
        return new DrainResult(resume, outcome.MaxRecordId, outcome.Collected);
    }

    /// <summary>EventLogReader を前方向に読み、各レコードを parse して <see cref="DrainRecord"/> へ落とす。</summary>
    private IEnumerable<DrainRecord> ReadDrainRecords(EventLogReader reader)
    {
        for (EventRecord? record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
        {
            using (record)
            {
                var parsed = TryReadPayload(record, out var payload) ? payload : null;
                yield return new DrainRecord(record.RecordId, record.Bookmark?.BookmarkXml, parsed);
            }
        }
    }

    /// <summary>
    /// 不在中サマリを提示し、成功後にチェックポイントを **1 回だけ** 確定する。提示(トースト表示)は
    /// 投げうるため広く握る — 失敗時はチェックポイントを進めず、次回起動で再ドレイン・再提示する
    /// (at-least-once。live 経路が提示後に advance するのと同じ順序で、取りこぼしより重複提示を選ぶ)。
    /// </summary>
    private void EmitWhileAwayAndCheckpoint(DrainResult drain)
    {
        if (drain.Collected.Count > 0)
        {
            try
            {
                WhileAwaySummaryReceived?.Invoke(WhileAwaySummary.Merge(drain.Collected));
            }
            catch (Exception ex)
            {
                Warning?.Invoke($"不在中サマリの表示に失敗(次回起動で再提示します): {ex.Message}");
                return; // チェックポイント未確定のまま(= 再提示可能)。
            }
        }

        // 提示成功(or 出すものなし)後に、ドレインで通過した最終位置へ 1 度だけ確定する
        // (per-record 保存を避け、長期不在後の起動を N 件のファイル書き込みでブロックしない)。
        if (drain.MaxRecordId is { } maxId)
            Advance(maxId, drain.ResumeBookmark);
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

            if (TryReadPayload(record, out var payload))
                SummaryReceived?.Invoke(payload!);

            Advance(id, record.Bookmark);
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
