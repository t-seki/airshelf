namespace Radirec.Gui.Core;

/// <summary>取込通知ペイロードからトーストの text 要素列(表題 + 本文行)を組み立てる(表示専用・純粋。ADR-0009/0011)。</summary>
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

    // ---- 取込要約(1001)----

    /// <summary>ライブ取込通知(「新規取込 N件」)の text 要素列。</summary>
    public static IReadOnlyList<string> BuildTexts(IngestSummaryPayload payload) =>
        Compose($"新規取込 {payload.TotalImported}件", FormatAlbums(payload), payload.Truncated);

    /// <summary>不在中サマリ(「不在中の取込 N件」)の text 要素列(While-Away Summary。ADR-0009 拡張)。</summary>
    public static IReadOnlyList<string> BuildWhileAwayTexts(IngestSummaryPayload payload) =>
        Compose($"不在中の取込 {payload.TotalImported}件", FormatAlbums(payload), payload.Truncated);

    // ---- 取込失敗(1002。ADR-0011)----

    /// <summary>ライブ取込失敗通知(「取込失敗 N件」)の text 要素列。</summary>
    public static IReadOnlyList<string> BuildFailureTexts(IngestFailurePayload payload) =>
        Compose($"取込失敗 {payload.FailedCount}件", FormatFiles(payload), payload.Truncated);

    /// <summary>不在中サマリの失敗版(「不在中の失敗 N件」)の text 要素列。</summary>
    public static IReadOnlyList<string> BuildWhileAwayFailureTexts(IngestFailurePayload payload) =>
        Compose($"不在中の失敗 {payload.FailedCount}件", FormatFiles(payload), payload.Truncated);

    /// <summary>
    /// 表題 + 本文行(整形済み)から text 要素列を組み立てる。要約・失敗・不在中で共通の切り詰めロジック
    /// (「…ほか」の付与・要素数上限)を 1 箇所に集約し、成功/失敗で挙動が drift しないようにする。
    /// </summary>
    private static IReadOnlyList<string> Compose(string title, IReadOnlyList<string> lines, bool truncated)
    {
        var texts = new List<string> { title };

        var hasMore = lines.Count > MaxLines || truncated;
        // 「…ほか」を出すなら 1 行分空ける。
        var toShow = hasMore ? MaxLines - 1 : Math.Min(lines.Count, MaxLines);
        texts.AddRange(lines.Take(toShow));

        // 本文を少なくとも 1 行示したときだけ「…ほか」。空リスト + truncated で**裸の「…ほか」**を出さない。
        if (hasMore && texts.Count > 1)
            texts.Add("…ほか");

        // 硬い上限(実機の text 要素上限)。ロジックが将来変わっても超えないための最終ガード。
        return texts.Take(MaxToastTextElements).ToList();
    }

    private static IReadOnlyList<string> FormatAlbums(IngestSummaryPayload payload) =>
        payload.Albums.Select(FormatAlbum).ToList();

    private static IReadOnlyList<string> FormatFiles(IngestFailurePayload payload) =>
        payload.Files.Select(FormatFailureFile).ToList();

    private static string FormatFailureFile(IngestFailureFilePayload file) =>
        string.IsNullOrEmpty(file.Reason) ? file.Name : $"{file.Name}（{file.Reason}）";

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
