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

    // ---- 取込要約(成功。ADR-0008)----

    /// <summary>
    /// 取込通知(「新規取込 N件」)の text 要素列。起動時 catch-up の集約もこれで表示する(ADR-0012)。
    /// 統合設定の無い兄弟フォルダは載せない(管理画面の要対応に出す。ADR-0018・#155)。
    /// </summary>
    public static IReadOnlyList<string> BuildTexts(IngestSummaryPayload payload) =>
        Compose($"新規取込 {payload.TotalImported}件", FormatAlbums(payload), payload.Truncated);

    // ---- 取込失敗(ADR-0011)----

    /// <summary>取込失敗通知(「取込失敗 N件」)の text 要素列。</summary>
    public static IReadOnlyList<string> BuildFailureTexts(IngestFailurePayload payload) =>
        Compose($"取込失敗 {payload.FailedCount}件", FormatFiles(payload), payload.Truncated);

    /// <summary>
    /// 表題 + 本文行(整形済み)から text 要素列を組み立てる。要約・失敗・不在中で共通の切り詰めロジック
    /// (「…ほか」の付与・要素数上限)を 1 箇所に集約し、成功/失敗で挙動が drift しないようにする。
    /// </summary>
    /// <remarks>
    /// <paramref name="lines"/> が使える行に収まらないとき(または <paramref name="truncated"/>)は「…ほか」を付ける。
    /// 使える行が 2 行以上なら最後の 1 行を「…ほか」に充て、1 行しかなければ先頭の行の末尾に「 …ほか」を付ける
    /// (1 行を「…ほか」に充てると本文を 1 つも示せなくなるため)。
    /// </remarks>
    private static IReadOnlyList<string> Compose(string title, IReadOnlyList<string> lines, bool truncated)
    {
        var texts = new List<string> { title };
        var budget = MaxLines;

        var hasMore = lines.Count > budget || truncated;
        if (!hasMore)
        {
            texts.AddRange(lines.Take(budget));
        }
        // 本文を少なくとも 1 行示すときだけ「…ほか」。空リスト + truncated で**裸の「…ほか」**を出さない。
        else if (lines.Count > 0 && budget >= 2)
        {
            texts.AddRange(lines.Take(budget - 1));
            texts.Add("…ほか");
        }
        else if (lines.Count > 0 && budget == 1)
        {
            texts.Add($"{lines[0]} …ほか");
        }

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
        if (album.EpisodeCount <= 0 || album.FirstEpisode is null)
            return $"{album.Program}（{album.Station}）";

        if (album.EpisodeCount == 1)
        {
            var e = album.FirstEpisode;
            var when = string.IsNullOrEmpty(e.BroadcastTime)
                ? e.BroadcastDate
                : $"{e.BroadcastDate} {e.BroadcastTime}";
            return $"{album.Program}（{album.Station}）{when}";
        }

        // 実数をそのまま出す。以前は打ち切り済みリストの長さを出していたため、
        // 1 番組で 20 回を超えると「新規取込 25件」の下に「番組(局)20件」が並ぶ嘘が出ていた(#100)。
        return $"{album.Program}（{album.Station}）{album.EpisodeCount}件";
    }
}
