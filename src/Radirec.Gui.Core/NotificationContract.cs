namespace Radirec.Gui.Core;

/// <summary>
/// 取込通知の受け手が依存する EventLog 契約の値(ADR-0008 / 0009)。発行側の
/// <c>Radirec.Hosting.RadirecEventLog</c> と一致している必要があり、一致は契約テストで保証する
/// (層構成上 GUI は Hosting を参照しないため、値は複製しテストでドリフトを検出する)。
/// </summary>
public static class NotificationContract
{
    /// <summary>購読対象の EventLog ソース名。</summary>
    public const string SourceName = "Radirec";

    /// <summary>取込要約シグナルの EventID。</summary>
    public const int IngestSummaryEventId = 1001;

    /// <summary>受け手が理解できる schemaVersion。これと一致しない版は未対応として扱う。</summary>
    public const int SupportedSchemaVersion = 1;
}
