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

    /// <summary>初めて取り込んだフォルダのうち、統合設定が無く兄弟フォルダのあるもの(#127)。</summary>
    public IReadOnlyList<SiblingFolderNoticePayload> SiblingFolderNotices { get; init; } = [];

    /// <summary>兄弟フォルダの知らせの総数(<see cref="SiblingFolderNotices"/> は上限で打ち切られうる。件数はこちらを出す)。</summary>
    public int SiblingFolderNoticeCount { get; init; }
}

/// <summary>統合設定の無い兄弟フォルダの知らせ 1 件(Core の <c>SiblingFolderNotice</c> を写したもの)。</summary>
public sealed record SiblingFolderNoticePayload
{
    /// <summary>初めて取り込んだフォルダ。</summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>兄弟フォルダが入っている統合番組のアルバム名。兄弟も統合されていなければ null。</summary>
    public string? MergedAlbum { get; init; }

    /// <summary>兄弟フォルダの組(<see cref="Folder"/> を含む)。</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];
}

/// <summary>要約中の 1 番組(= Jellyfin の 1 アルバム)。</summary>
public sealed record IngestSummaryAlbumPayload
{
    public string Station { get; init; } = string.Empty;
    public string Program { get; init; } = string.Empty;

    /// <summary>取り込んだ放送回の数(打ち切らない実数。表示はこれを出す)。</summary>
    public int EpisodeCount { get; init; }

    /// <summary><see cref="EpisodeCount"/> が 1 のときに放送日時を出すために持つ。無ければ番組名のみ表示。</summary>
    public IngestSummaryEpisodePayload? FirstEpisode { get; init; }
}

/// <summary>要約中の 1 放送回。放送日は <c>yyyy-MM-dd</c>、放送時刻は <c>HH:mm</c>(取得不能なら null)。</summary>
public sealed record IngestSummaryEpisodePayload
{
    public string BroadcastDate { get; init; } = string.Empty;
    public string? BroadcastTime { get; init; }
    public int PartIndex { get; init; }
}
