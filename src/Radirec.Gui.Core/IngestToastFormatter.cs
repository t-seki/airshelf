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
    /// 統合設定の無い兄弟フォルダの知らせ(#127)があれば、本文の最後の 1 行をそれに充てる(トーストは増やさない)。
    /// </summary>
    public static IReadOnlyList<string> BuildTexts(IngestSummaryPayload payload)
    {
        var title = $"新規取込 {payload.TotalImported}件";
        if (payload.SiblingFolderNotices.Count == 0)
            return Compose(title, FormatAlbums(payload), payload.Truncated);

        // 番組の行は残り 1 行に畳む。Compose に 1 行で渡すと「…ほか」の分を空けて番組を 1 つも出せなくなるので、
        // 2 番組以上なら先頭の番組の行に「…ほか」を付ける。
        var texts = new List<string> { title };
        var albums = FormatAlbums(payload);
        if (albums.Count > 0)
            texts.Add(albums.Count > 1 || payload.Truncated ? $"{albums[0]} …ほか" : albums[0]);
        texts.Add(FormatSiblingFolderNotices(payload.SiblingFolderNotices));

        return texts.Take(MaxToastTextElements).ToList();
    }

    // ---- 統合設定の無い兄弟フォルダ(#127)----

    /// <summary>
    /// 統合設定の無い兄弟フォルダの知らせ 1 件の文言。取込通知の 1 行と <c>gui.log</c> で同じものを使う。
    /// </summary>
    /// <param name="folder">初めて取り込んだフォルダ。</param>
    /// <param name="mergedAlbum">兄弟フォルダが入っている統合番組のアルバム名(兄弟も統合されていなければ null)。</param>
    /// <param name="folders">兄弟フォルダの組(<paramref name="folder"/> を含む)。</param>
    public static string FormatSiblingFolderNotice(string folder, string? mergedAlbum, IReadOnlyList<string> folders) =>
        mergedAlbum is not null
            ? $"統合番組『{mergedAlbum}』に入っていないフォルダ『{folder}』があります"
            : $"分割番組らしいフォルダ{string.Concat(folders.Select(f => $"『{f}』"))}に統合設定がありません";

    // 取込通知に充てるのは 1 行だけ。2 件以上あれば先頭の 1 件に件数を添える(全件は gui.log にある)。
    private static string FormatSiblingFolderNotices(IReadOnlyList<SiblingFolderNoticePayload> notices)
    {
        var first = notices[0];
        var text = FormatSiblingFolderNotice(first.Folder, first.MergedAlbum, first.Folders);
        return notices.Count > 1 ? $"{text}（ほか{notices.Count - 1}件）" : text;
    }

    // ---- 取込失敗(ADR-0011)----

    /// <summary>取込失敗通知(「取込失敗 N件」)の text 要素列。</summary>
    public static IReadOnlyList<string> BuildFailureTexts(IngestFailurePayload payload) =>
        Compose($"取込失敗 {payload.FailedCount}件", FormatFiles(payload), payload.Truncated);

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
