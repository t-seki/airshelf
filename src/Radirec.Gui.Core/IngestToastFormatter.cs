namespace Radirec.Gui.Core;

/// <summary>取込要約ペイロードからトーストの表題・本文行を組み立てる(表示専用・純粋。ADR-0009)。</summary>
public static class IngestToastFormatter
{
    /// <summary>本文行の上限。WinRT トーストの text 要素数制約に収めるため小さく保つ(表題 + 本文で計 4 以内)。</summary>
    public const int MaxLines = 3;

    /// <summary>ペイロードから表題と本文行(改行なしの文字列群)を組み立てる。受け手は行ごとに表示する。</summary>
    public static (string Title, IReadOnlyList<string> Lines) Format(IngestSummaryPayload payload)
    {
        var title = $"新規取込 {payload.TotalImported}件";

        var hasMore = payload.Albums.Count > MaxLines || payload.Truncated;
        // 「…ほか」を出すなら 1 行分空ける。
        var albumsToShow = hasMore ? MaxLines - 1 : Math.Min(payload.Albums.Count, MaxLines);

        var lines = payload.Albums.Take(albumsToShow).Select(FormatAlbum).ToList();
        if (hasMore)
            lines.Add("…ほか");

        return (title, lines);
    }

    private static string FormatAlbum(IngestSummaryAlbumPayload album)
    {
        // 発行側の不変条件では album は必ず 1 回以上を持つが、防御的に空も扱う(番組名のみ)。
        if (album.Episodes.Count == 0)
            return $"{album.Program}（{album.Station}）";

        if (album.Episodes.Count == 1)
        {
            var e = album.Episodes[0];
            var when = string.IsNullOrEmpty(e.BroadcastTime)
                ? e.BroadcastDate
                : $"{e.BroadcastDate} {e.BroadcastTime}";
            return $"{album.Program}（{album.Station}）{when}";
        }

        return $"{album.Program}（{album.Station}）{album.Episodes.Count}件";
    }
}
