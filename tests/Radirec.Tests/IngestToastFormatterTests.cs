using Radirec.Gui.Core;

namespace Radirec.Tests;

public sealed class IngestToastFormatterTests
{
    private static IngestSummaryPayload Payload(
        int total, bool truncated, params IngestSummaryAlbumPayload[] albums) => new()
    {
        TotalImported = total,
        Truncated = truncated,
        Albums = albums,
    };

    // 表示は「件数」と「1 件目の放送日時」だけを使う(#100)。テストもその形で組む。
    private static IngestSummaryAlbumPayload Album(
        string station, string program, params IngestSummaryEpisodePayload[] episodes) =>
        new()
        {
            Station = station,
            Program = program,
            EpisodeCount = episodes.Length,
            FirstEpisode = episodes.FirstOrDefault(),
        };

    /// <summary>件数だけを指定する(実数が大きく、1 件目の日時は表示に出ないケース)。</summary>
    private static IngestSummaryAlbumPayload AlbumOf(string station, string program, int count) =>
        new()
        {
            Station = station,
            Program = program,
            EpisodeCount = count,
            FirstEpisode = new IngestSummaryEpisodePayload { BroadcastDate = "2026-01-01" },
        };

    private static IngestSummaryEpisodePayload Ep(string date, string? time = null, int part = 0) =>
        new() { BroadcastDate = date, BroadcastTime = time, PartIndex = part };

    // BuildTexts の先頭が表題、以降が本文行。
    private static string Title(IReadOnlyList<string> texts) => texts[0];
    private static IReadOnlyList<string> Body(IReadOnlyList<string> texts) => texts.Skip(1).ToList();

    [Fact]
    public void 単一番組単一回は放送日時を出す()
    {
        var texts = IngestToastFormatter.BuildTexts(
            Payload(1, false, Album("J-WAVE", "番組A", Ep("2026-07-10", "22:30"))));

        Assert.Equal("新規取込 1件", Title(texts));
        Assert.Equal(["番組A（J-WAVE）2026-07-10 22:30"], Body(texts));
    }

    [Fact]
    public void 放送時刻無しは日付のみ()
    {
        var texts = IngestToastFormatter.BuildTexts(
            Payload(1, false, Album("局", "番組", Ep("2026-01-01"))));

        Assert.Equal(["番組（局）2026-01-01"], Body(texts));
    }

    [Fact]
    public void 同一番組複数回は件数表記()
    {
        var texts = IngestToastFormatter.BuildTexts(
            Payload(2, false, Album("TBS", "番組", Ep("2026-01-01", "08:30"), Ep("2026-01-01", "10:00"))));

        Assert.Equal(["番組（TBS）2件"], Body(texts));
    }

    [Fact]
    public void 空episodesは番組名のみ()
    {
        var texts = IngestToastFormatter.BuildTexts(Payload(0, false, Album("局", "番組")));

        Assert.Equal(["番組（局）"], Body(texts));
    }

    [Fact]
    public void 件数は実数をそのまま出す_打ち切らない()
    {
        // 回帰(#100): かつては要約側が 1 番組 20 回で打ち切り、その**リストの長さ**を件数として
        // 出していたため、「新規取込 25件」の下に「番組(局)20件」という嘘が並んだ。
        var texts = IngestToastFormatter.BuildTexts(Payload(25, false, AlbumOf("局", "番組", 25)));

        Assert.Equal("新規取込 25件", Title(texts));
        Assert.Equal(["番組（局）25件"], Body(texts));
    }

    [Fact]
    public void 複数番組は行で並べる()
    {
        var texts = IngestToastFormatter.BuildTexts(Payload(2, false,
            Album("局1", "番組1", Ep("2026-01-01", "10:00")),
            Album("局2", "番組2", Ep("2026-01-02", "11:00"))));

        Assert.Equal("新規取込 2件", Title(texts));
        Assert.Equal(["番組1（局1）2026-01-01 10:00", "番組2（局2）2026-01-02 11:00"], Body(texts));
    }

    [Fact]
    public void 上限超過はほかを添える()
    {
        // 3 番組以上は 先頭 1 番組 +「…ほか」に畳む(表題 + 本文 2 = 計 3 要素)。
        var texts = IngestToastFormatter.BuildTexts(Payload(3, false,
            Album("局1", "番組1", Ep("2026-01-01", "10:00")),
            Album("局2", "番組2", Ep("2026-01-02", "11:00")),
            Album("局3", "番組3", Ep("2026-01-03", "12:00"))));

        Assert.Equal(["番組1（局1）2026-01-01 10:00", "…ほか"], Body(texts));
    }

    [Fact]
    public void truncatedはほかを添える()
    {
        var texts = IngestToastFormatter.BuildTexts(
            Payload(999, truncated: true, Album("局", "番組", Ep("2026-01-01", "10:00"))));

        Assert.Equal("…ほか", Body(texts)[^1]);
    }

    [Fact]
    public void 大量番組でも要素数の硬い上限を超えない()
    {
        // 表題 + 本文行 が WinRT の text 要素上限(実機で 4 は例外)を超えないこと。
        var albums = Enumerable.Range(1, 50)
            .Select(i => Album($"局{i}", $"番組{i}", Ep("2026-01-01", "10:00")))
            .ToArray();

        var texts = IngestToastFormatter.BuildTexts(Payload(albums.Length, false, albums));

        Assert.True(texts.Count <= IngestToastFormatter.MaxToastTextElements);
        Assert.True(IngestToastFormatter.MaxToastTextElements < 4,
            "権威となる上限自体が実機の失敗閾値(4)未満であること");
    }

    // ---- 統合設定の無い兄弟フォルダ(#127)----

    private static SiblingFolderNoticePayload Unintegrated(params string[] folders) =>
        new() { Folder = folders[^1], Folders = folders };

    private static SiblingFolderNoticePayload OutsideMerged(string album, string folder, params string[] folders) =>
        new() { Folder = folder, MergedAlbum = album, Folders = folders };

    [Fact]
    public void 兄弟も統合されていなければ統合設定がありませんの1行を足す()
    {
        var texts = IngestToastFormatter.BuildTexts(
            Payload(1, false, Album("TBSラジオ", "X(2)", Ep("2026-09-20", "15:00"))) with
            {
                SiblingFolderNotices = [Unintegrated("X(1)", "X(2)")],
            });

        Assert.Equal("新規取込 1件", Title(texts));
        Assert.Equal(
            ["X(2)（TBSラジオ）2026-09-20 15:00", "分割番組らしいフォルダ『X(1)』『X(2)』に統合設定がありません"],
            Body(texts));
    }

    [Fact]
    public void 兄弟が統合番組に入っていれば統合番組の文言にする()
    {
        var texts = IngestToastFormatter.BuildTexts(
            Payload(1, false, Album("文化放送", "おとなりさん 11時12時", Ep("2026-09-23", "11:00"))) with
            {
                SiblingFolderNotices =
                [
                    OutsideMerged("おとなりさん", "おとなりさん 11時12時", "おとなりさん 10時11時", "おとなりさん 11時12時"),
                ],
            });

        Assert.Equal("統合番組『おとなりさん』に入っていないフォルダ『おとなりさん 11時12時』があります", Body(texts)[^1]);
    }

    [Fact]
    public void 知らせがあり番組が複数なら番組の行を1行に畳んで要素数の上限を守る()
    {
        var texts = IngestToastFormatter.BuildTexts(
            Payload(3, false,
                Album("局1", "番組1", Ep("2026-01-01", "10:00")),
                Album("局2", "番組2", Ep("2026-01-02", "11:00"))) with
            {
                SiblingFolderNotices = [Unintegrated("X(1)", "X(2)"), Unintegrated("Y(1)", "Y(2)")],
            });

        Assert.Equal(IngestToastFormatter.MaxToastTextElements, texts.Count);
        Assert.Equal(
            [
                "番組1（局1）2026-01-01 10:00 …ほか",
                "分割番組らしいフォルダ『X(1)』『X(2)』に統合設定がありません（ほか1件）",
            ],
            Body(texts));
    }

    [Fact]
    public void 知らせの件数は打ち切られた一覧ではなく総数から出す()
    {
        var texts = IngestToastFormatter.BuildTexts(
            Payload(1, false, Album("局", "番組", Ep("2026-01-01", "10:00"))) with
            {
                SiblingFolderNotices = [Unintegrated("X(1)", "X(2)")],
                SiblingFolderNoticeCount = 30,
            });

        Assert.Equal("分割番組らしいフォルダ『X(1)』『X(2)』に統合設定がありません（ほか29件）", Body(texts)[^1]);
    }

    [Fact]
    public void 知らせがあり番組が無ければ知らせの行だけ()
    {
        var texts = IngestToastFormatter.BuildTexts(
            Payload(0, true) with { SiblingFolderNotices = [Unintegrated("X(1)", "X(2)")] });

        Assert.Equal(["分割番組らしいフォルダ『X(1)』『X(2)』に統合設定がありません"], Body(texts));
    }

    [Fact]
    public void 知らせが無ければ本文は今までどおり()
    {
        var payload = Payload(2, false,
            Album("局1", "番組1", Ep("2026-01-01", "10:00")),
            Album("局2", "番組2", Ep("2026-01-02", "11:00")));

        Assert.Equal(["番組1（局1）2026-01-01 10:00", "番組2（局2）2026-01-02 11:00"], Body(IngestToastFormatter.BuildTexts(payload)));
    }
}
