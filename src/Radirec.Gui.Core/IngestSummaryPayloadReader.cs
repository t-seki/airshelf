using System.Text.Json;

namespace Radirec.Gui.Core;

/// <summary>取込要約シグナルの JSON ペイロードを parse する。schemaVersion で互換を判定する(ADR-0009)。</summary>
public static class IngestSummaryPayloadReader
{
    /// <summary>受け手が理解できる schemaVersion。</summary>
    public const int SupportedSchemaVersion = NotificationContract.SupportedSchemaVersion;

    private static readonly JsonSerializerOptions Options = NotificationContract.PayloadJsonOptions;

    /// <summary>
    /// JSON を <see cref="IngestSummaryPayload"/> に解析する。成功時 true。
    /// 解析失敗・空・schemaVersion 不一致は false を返し <paramref name="error"/> に理由を入れる
    /// (受け手はトーストを出さずログに残す想定)。同一 schemaVersion 内の未知フィールドは無視する
    /// (前方互換)。schemaVersion が上がった場合は破壊的変更として弾く(発行側と同梱配布のため実運用で不一致は稀)。
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

        // STJ は明示的な JSON null に対して init の `= []` 既定を上書きして null を入れる。
        // 常駐プロセスを守るため null コレクションを空へ正規化してから返す(下流の NRE を防ぐ)。
        payload = Normalize(parsed);
        return true;
    }

    private static IngestSummaryPayload Normalize(IngestSummaryPayload p) => p with
    {
        Albums = (p.Albums ?? [])
            .Where(a => a is not null)
            .Select(a => a with { Episodes = (a.Episodes ?? []).Where(e => e is not null).ToList() })
            .ToList(),
    };
}
