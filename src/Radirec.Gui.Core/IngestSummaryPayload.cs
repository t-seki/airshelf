namespace Radirec.Gui.Core;

/// <summary>
/// 取込要約シグナル(EventLog EventID 1001)の JSON envelope に対応する受け手側 DTO(ADR-0008 / 0009)。
/// 発行側(Hosting)の C# 型は共有せず、バージョン付き JSON 契約として受ける。ドリフトは契約テストで検出する。
/// </summary>
public sealed record IngestSummaryPayload
{
    public int SchemaVersion { get; init; }
    public int TotalImported { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<IngestSummaryAlbumPayload> Albums { get; init; } = [];
}

/// <summary>要約中の 1 番組(= Jellyfin の 1 アルバム)。</summary>
public sealed record IngestSummaryAlbumPayload
{
    public string Station { get; init; } = string.Empty;
    public string Program { get; init; } = string.Empty;
    public IReadOnlyList<IngestSummaryEpisodePayload> Episodes { get; init; } = [];
}

/// <summary>要約中の 1 放送回。放送日は <c>yyyy-MM-dd</c>、放送時刻は <c>HH:mm</c>(取得不能なら null)。</summary>
public sealed record IngestSummaryEpisodePayload
{
    public string BroadcastDate { get; init; } = string.Empty;
    public string? BroadcastTime { get; init; }
    public int PartIndex { get; init; }
}
