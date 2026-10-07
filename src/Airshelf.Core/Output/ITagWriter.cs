using Airshelf.Core.Model;

namespace Airshelf.Core.Output;

/// <summary>
/// 出力ファイルへ正規化タグを書き込む。フォーマット差(MP4 / MP3)を隠蔽する。
/// </summary>
public interface ITagWriter
{
    void Write(string filePath, NormalizedTags tags);

    /// <summary>
    /// アルバム内の置き場所を表すタグ(<c>album</c> / <c>title</c> / <c>track</c>)だけを書き換え、他のタグは残す
    /// (統合設定の再適用。ADR-0016)。取り込み済みの回は出演者などを利用者が直している場合があるため、
    /// <see cref="Write"/> で全部を書き直さない。
    /// </summary>
    void WritePlacement(string filePath, NormalizedTags tags);
}
