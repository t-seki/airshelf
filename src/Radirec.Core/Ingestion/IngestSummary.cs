using Radirec.Core.Model;

namespace Radirec.Core.Ingestion;

/// <summary>
/// 取込要約シグナルのペイロード。1 走査の新規取込を番組(アルバム)単位にまとめた domain 表現。
/// transport(EventLog 等)非依存で、番組数・放送回数の上限によりサイズを有界化する(ADR-0008)。
/// </summary>
public sealed record IngestSummary
{
    /// <summary>番組(アルバム)の上限。超過分は落とし <see cref="Truncated"/> を立てる。</summary>
    public const int MaxAlbums = 20;

    /// <summary>1 番組あたりの放送回の上限。</summary>
    public const int MaxEpisodesPerAlbum = 20;

    /// <summary>この走査で新規取込した放送回の総数(キャップ前の実数)。</summary>
    public required int TotalImported { get; init; }

    /// <summary>キャップにより albums / episodes を間引いたか。</summary>
    public required bool Truncated { get; init; }

    /// <summary>番組(アルバム)単位のまとまり(最大 <see cref="MaxAlbums"/> 件)。</summary>
    public required IReadOnlyList<IngestSummaryAlbum> Albums { get; init; }

    /// <summary>新規取込した放送回群から要約を組み立てる(番組単位グループ + キャップ)。出現順を保つ。</summary>
    public static IngestSummary From(IReadOnlyList<Episode> imported)
    {
        var truncated = false;
        var groups = new List<(string Station, string Program, List<IngestSummaryEpisode> Episodes)>();
        var index = new Dictionary<(string Station, string Program), int>();

        foreach (var e in imported)
        {
            var key = (e.Station, e.Program);
            if (!index.TryGetValue(key, out var i))
            {
                // 新規番組が上限を超えるなら落として truncated を立てる。
                if (groups.Count >= MaxAlbums)
                {
                    truncated = true;
                    continue;
                }

                i = groups.Count;
                index[key] = i;
                groups.Add((e.Station, e.Program, []));
            }

            var episodes = groups[i].Episodes;
            if (episodes.Count >= MaxEpisodesPerAlbum)
            {
                truncated = true;
                continue;
            }

            episodes.Add(new IngestSummaryEpisode
            {
                BroadcastDate = e.BroadcastDate,
                BroadcastTime = e.BroadcastTime,
                PartIndex = e.PartIndex ?? 0,
            });
        }

        return new IngestSummary
        {
            TotalImported = imported.Count,
            Truncated = truncated,
            Albums = groups
                .Select(g => new IngestSummaryAlbum
                {
                    Station = g.Station,
                    Program = g.Program,
                    Episodes = g.Episodes,
                })
                .ToList(),
        };
    }
}

/// <summary>要約中の 1 番組(= Jellyfin の 1 アルバム)。</summary>
public sealed record IngestSummaryAlbum
{
    public required string Station { get; init; }
    public required string Program { get; init; }
    public required IReadOnlyList<IngestSummaryEpisode> Episodes { get; init; }
}

/// <summary>要約中の 1 放送回。</summary>
public sealed record IngestSummaryEpisode
{
    public required DateOnly BroadcastDate { get; init; }

    /// <summary>放送時刻(JST。取得不能なら null)。同日別時刻の別放送を区別するための識別子(ADR-0002)。</summary>
    public TimeOnly? BroadcastTime { get; init; }

    public required int PartIndex { get; init; }
}
