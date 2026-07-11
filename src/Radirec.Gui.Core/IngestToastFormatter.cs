namespace Radirec.Gui.Core;

/// <summary>取込要約ペイロードからトーストの表題・本文を組み立てる(表示専用・純粋。ADR-0009)。</summary>
public static class IngestToastFormatter
{
    /// <summary>本文に列挙する番組行の上限。超過分は「…ほか」に畳む。</summary>
    public const int MaxLines = 3;

    /// <summary>ペイロードから (表題, 本文) を組み立てる。</summary>
    public static (string Title, string Body) Format(IngestSummaryPayload payload)
    {
        var title = $"新規取込 {payload.TotalImported}件";

        var lines = new List<string>();
        foreach (var album in payload.Albums.Take(MaxLines))
        {
            lines.Add(FormatAlbum(album));
        }

        // 表示上限で溢れた、または発行側で件数キャップされた場合は「ほか」を添える。
        if (payload.Albums.Count > MaxLines || payload.Truncated)
        {
            lines.Add("…ほか");
        }

        return (title, string.Join("\n", lines));
    }

    private static string FormatAlbum(IngestSummaryAlbumPayload album)
    {
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
