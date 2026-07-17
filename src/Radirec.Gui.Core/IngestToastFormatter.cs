namespace Radirec.Gui.Core;

/// <summary>取込要約ペイロードからトーストの text 要素列(表題 + 本文行)を組み立てる(表示専用・純粋。ADR-0009)。</summary>
public static class IngestToastFormatter
{
    /// <summary>
    /// トーストの &lt;text&gt; 要素(表題を含む)の硬い上限。これを超えると
    /// <c>ToastContentBuilder</c> が "We have reached max lines allowed (4) per toast" で失敗する
    /// (実機確認: 4 要素で例外)。安全側で総 3 要素に抑える。**この値が唯一の権威**。
    /// </summary>
    public const int MaxToastTextElements = 3;

    /// <summary>本文行の上限(表題 1 要素を除いた残り)。<see cref="MaxToastTextElements"/> から導出する。</summary>
    public const int MaxLines = MaxToastTextElements - 1;

    /// <summary>
    /// トーストに渡す text 要素列を返す(先頭 = 表題、以降 = 本文行)。<see cref="MaxToastTextElements"/> に
    /// 切り詰め済みで、受け手はこれを順に AddText するだけでよい(要素数上限を受け手が意識しなくて済む)。
    /// ライブ通知は「新規取込 N件」。不在中サマリは <see cref="BuildWhileAwayTexts"/> で表題だけ変える。
    /// </summary>
    public static IReadOnlyList<string> BuildTexts(IngestSummaryPayload payload) =>
        BuildTexts(payload, $"新規取込 {payload.TotalImported}件");

    /// <summary>
    /// 不在中サマリ(受け手停止中に溜まった取込をマージした 1 件)の text 要素列を返す。本文の組み立ては
    /// ライブ通知と共通で、表題だけ「不在中の取込 N件」に差し替える(While-Away Summary。ADR-0009 拡張)。
    /// </summary>
    public static IReadOnlyList<string> BuildWhileAwayTexts(IngestSummaryPayload payload) =>
        BuildTexts(payload, $"不在中の取込 {payload.TotalImported}件");

    /// <summary>取込失敗のライブ通知(「取込失敗 N件」)の text 要素列。ADR-0011。</summary>
    public static IReadOnlyList<string> BuildFailureTexts(IngestFailurePayload payload) =>
        BuildFailureTexts(payload, $"取込失敗 {payload.FailedCount}件");

    /// <summary>不在中サマリの失敗版(「不在中の失敗 N件」)の text 要素列。ADR-0011。</summary>
    public static IReadOnlyList<string> BuildWhileAwayFailureTexts(IngestFailurePayload payload) =>
        BuildFailureTexts(payload, $"不在中の失敗 {payload.FailedCount}件");

    private static IReadOnlyList<string> BuildFailureTexts(IngestFailurePayload payload, string title)
    {
        var texts = new List<string> { title };

        var hasMore = payload.Files.Count > MaxLines || payload.Truncated;
        var filesToShow = hasMore ? MaxLines - 1 : Math.Min(payload.Files.Count, MaxLines);

        texts.AddRange(payload.Files.Take(filesToShow).Select(FormatFailureFile));
        if (hasMore)
            texts.Add("…ほか");

        return texts.Take(MaxToastTextElements).ToList();
    }

    private static string FormatFailureFile(IngestFailureFilePayload file) =>
        string.IsNullOrEmpty(file.Reason) ? file.Name : $"{file.Name}（{file.Reason}）";

    private static IReadOnlyList<string> BuildTexts(IngestSummaryPayload payload, string title)
    {
        var texts = new List<string> { title };

        var hasMore = payload.Albums.Count > MaxLines || payload.Truncated;
        // 「…ほか」を出すなら 1 行分空ける。
        var albumsToShow = hasMore ? MaxLines - 1 : Math.Min(payload.Albums.Count, MaxLines);

        texts.AddRange(payload.Albums.Take(albumsToShow).Select(FormatAlbum));
        if (hasMore)
            texts.Add("…ほか");

        // 硬い上限(実機の text 要素上限)。formatter ロジックが将来変わっても超えないための最終ガード。
        return texts.Take(MaxToastTextElements).ToList();
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
