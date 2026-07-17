using System.Text.Json;

namespace Radirec.Gui.Core;

/// <summary>取込失敗シグナル(1002)の JSON ペイロードを parse する。schemaVersion で互換を判定する(ADR-0011)。</summary>
public static class IngestFailurePayloadReader
{
    /// <summary>受け手が理解できる失敗 schemaVersion。</summary>
    public const int SupportedSchemaVersion = NotificationContract.SupportedFailureSchemaVersion;

    private static readonly JsonSerializerOptions Options = NotificationContract.PayloadJsonOptions;

    /// <summary>
    /// JSON を <see cref="IngestFailurePayload"/> に解析する。成功時 true。解析失敗・空・schemaVersion 不一致は
    /// false を返し <paramref name="error"/> に理由を入れる(受け手はトーストを出さずログに残す想定)。
    /// null コレクションは空へ正規化してから返す(下流の NRE を防ぐ)。
    /// </summary>
    public static bool TryParse(string json, out IngestFailurePayload? payload, out string? error)
    {
        payload = null;
        error = null;

        IngestFailurePayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<IngestFailurePayload>(json, Options);
        }
        catch (JsonException ex)
        {
            error = $"JSON 解析に失敗: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "ペイロードが空です";
            return false;
        }

        if (parsed.SchemaVersion != SupportedSchemaVersion)
        {
            error = $"未対応の failure schemaVersion={parsed.SchemaVersion}(対応={SupportedSchemaVersion})";
            return false;
        }

        // null コレクション/null フィールドを正規化する(下流の NRE・トースト AddText(null) を防ぐ)。
        payload = parsed with
        {
            Files = (parsed.Files ?? [])
                .Where(f => f is not null)
                .Select(f => f with { Name = f.Name ?? string.Empty, Reason = f.Reason ?? string.Empty })
                .ToList(),
        };
        return true;
    }
}
