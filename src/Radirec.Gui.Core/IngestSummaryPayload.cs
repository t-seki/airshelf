namespace Radirec.Gui.Core;

/// <summary>
/// 取込要約をトースト表示するための presentation view model(ADR-0012)。トレイ常駐移行で取込通知は
/// in-process 化したため、Core の <c>IngestSummary</c> を publisher 側で本型へ写して
/// <see cref="IngestToastFormatter"/> に渡す(放送日/時刻は表示用の文字列で持つ)。
/// </summary>
public sealed record IngestSummaryPayload
{
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
