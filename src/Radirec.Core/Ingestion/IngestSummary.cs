using Radirec.Core.Model;

namespace Radirec.Core.Ingestion;

/// <summary>
/// 取込要約シグナルのペイロード。1 走査の新規取込を番組(アルバム)単位にまとめた domain 表現。
/// 提示の手段に依存しない。
/// </summary>
public sealed record IngestSummary
{
    /// <summary>
    /// 運ぶ番組(アルバム)の上限。超過分は落とし <see cref="Truncated"/> を立てる。
    /// <para>
    /// **これは表示のための値ではなく、payload を有界にする安全弁**。元は EventLog の ~32KB 制約に
    /// 合わせた値だったが、EventLog は ADR-0012 で撤去済み。現在の提示側(<c>IngestToastFormatter</c>)は
    /// 本文を 2 行までに絞るので、この値が 3 以上なら表示は変わらない(#100)。
    /// 初回 catch-up で数百番組が一度に入っても payload が膨らまないことだけを保証する。
    /// </para>
    /// </summary>
    public const int MaxAlbums = 20;

    /// <summary>この走査で新規取込した放送回の総数(キャップ前の実数)。</summary>
    public required int TotalImported { get; init; }

    /// <summary>
    /// <see cref="MaxAlbums"/> により番組を間引いたか。**放送回の件数は間引かない**ので、
    /// これが立つのは番組数が上限を超えたときだけ(#100)。
    /// </summary>
    public required bool Truncated { get; init; }

    /// <summary>番組(アルバム)単位のまとまり(最大 <see cref="MaxAlbums"/> 件)。</summary>
    public required IReadOnlyList<IngestSummaryAlbum> Albums { get; init; }

    /// <summary>新規取込した放送回群から要約を組み立てる(番組単位グループ + キャップ)。出現順を保つ。</summary>
    public static IngestSummary From(IReadOnlyList<Episode> imported)
    {
        var truncated = false;
        // 番組ごとに「実数」と「先頭 1 件」だけを持つ。放送回リストは持たない — 提示側が使うのは
        // 件数と(1 件のときだけ)その放送日時で、リストの中身は誰も読まないため(#100)。
        var groups = new List<(string Station, string Program, int Count, IngestSummaryEpisode First)>();
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
                groups.Add((e.Station, e.Program, 0, new IngestSummaryEpisode
                {
                    BroadcastDate = e.BroadcastDate,
                    BroadcastTime = e.BroadcastTime,
                    PartIndex = e.PartIndex ?? 0,
                }));
            }

            // 件数は打ち切らない。ここを打ち切ると表示の「N件」が実数とずれる(#100 で踏んだバグ)。
            var g = groups[i];
            groups[i] = (g.Station, g.Program, g.Count + 1, g.First);
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
                    EpisodeCount = g.Count,
                    FirstEpisode = g.First,
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

    /// <summary>
    /// この走査でこの番組から取り込んだ放送回の数。**打ち切らない実数**で、提示はこれをそのまま出す。
    /// (放送回のリストは持たない — 提示側が読むのは件数と <see cref="FirstEpisode"/> だけ。#100)
    /// </summary>
    public required int EpisodeCount { get; init; }

    /// <summary>先頭の放送回。<see cref="EpisodeCount"/> が 1 のとき、件数の代わりに放送日時を出すために持つ。</summary>
    public required IngestSummaryEpisode FirstEpisode { get; init; }
}

/// <summary>要約中の 1 放送回。</summary>
public sealed record IngestSummaryEpisode
{
    public required DateOnly BroadcastDate { get; init; }

    /// <summary>放送時刻(JST。取得不能なら null)。同日別時刻の別放送を区別するための識別子(ADR-0002)。</summary>
    public TimeOnly? BroadcastTime { get; init; }

    public required int PartIndex { get; init; }
}
