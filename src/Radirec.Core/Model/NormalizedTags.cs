namespace Radirec.Core.Model;

/// <summary>
/// 出力ファイルへ書き込む正規化済みタグ集合(案A / Jellyfin Music 向け)。
/// <see cref="Disc"/> / <see cref="Track"/> を <see cref="ushort"/> にすることで、
/// MP4 の 16bit 制約を型で守り「track=YYYYMMDD 統合(オーバーフロー)」を構造的に防ぐ。
/// </summary>
public sealed record NormalizedTags
{
    /// <summary>アルバムアーティスト = 放送局(グルーピングの安定キー)。</summary>
    public required string AlbumArtist { get; init; }

    /// <summary>アルバム = 番組名(1 番組 = 1 アルバム)。</summary>
    public required string Album { get; init; }

    /// <summary>アーティスト = 出演者(維持。無ければ null)。</summary>
    public string? Artist { get; init; }

    /// <summary>タイトル = 放送日 "YYYY-MM-DD"。</summary>
    public required string Title { get; init; }

    /// <summary>日付 = 放送日。</summary>
    public required DateOnly Date { get; init; }

    /// <summary>ディスク番号 = 放送年(例 2026)。</summary>
    public required ushort Disc { get; init; }

    /// <summary>トラック番号 = MMDD(例 6/12 → 612)。年内を日付順にソートする。</summary>
    public required ushort Track { get; init; }

    /// <summary>ジャンル(既定 "ラジオ")。</summary>
    public required string Genre { get; init; }

    /// <summary>コメント = 録音種別など由来情報(無ければ null)。</summary>
    public string? Comment { get; init; }
}
