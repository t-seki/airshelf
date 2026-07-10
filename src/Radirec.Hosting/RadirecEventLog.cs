namespace Radirec.Hosting;

/// <summary>
/// サービスのログ出力と取込要約シグナルが共有する Windows イベントログ契約(ADR-0008)。
/// ソース名は <c>ServiceCommand</c> の <c>AddEventLog</c> 登録・publisher の書き込み・
/// 受け手(GUI)の Source フィルタの三者が一致する必要があるため、単一の const に集約する。
/// </summary>
public static class RadirecEventLog
{
    /// <summary>EventLog ソース名(サービス登録時に登録される)。</summary>
    public const string SourceName = "Radirec";

    /// <summary>取込要約シグナルに予約した EventID(1000 番台を将来シグナル用に確保)。</summary>
    public const int IngestSummaryEventId = 1001;

    /// <summary>取込要約ペイロード JSON envelope のスキーマ版(前方進化用)。</summary>
    public const int SummarySchemaVersion = 1;
}
