using System.Text.Json;

namespace Radirec.Gui.Core;

/// <summary>取込要約シグナルの JSON ペイロードを parse する。schemaVersion で互換を判定する(ADR-0009)。</summary>
public static class IngestSummaryPayloadReader
{
    /// <summary>受け手が理解できる schemaVersion。発行側がこれを超える版を出したら未対応として弾く。</summary>
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// JSON を <see cref="IngestSummaryPayload"/> に解析する。成功時 true。
    /// 解析失敗・空・未対応 schemaVersion は false を返し <paramref name="error"/> に理由を入れる
    /// (受け手はトーストを出さずログに残す想定)。
    /// </summary>
    public static bool TryParse(string json, out IngestSummaryPayload? payload, out string? error)
    {
        payload = null;
        error = null;

        IngestSummaryPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<IngestSummaryPayload>(json, Options);
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
            error = $"未対応の schemaVersion={parsed.SchemaVersion}(対応={SupportedSchemaVersion})";
            return false;
        }

        payload = parsed;
        return true;
    }
}
