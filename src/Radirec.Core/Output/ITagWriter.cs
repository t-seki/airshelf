using Radirec.Core.Model;

namespace Radirec.Core.Output;

/// <summary>
/// 出力ファイルへ正規化タグを書き込む。フォーマット差(MP4 / MP3)を隠蔽する。
/// </summary>
public interface ITagWriter
{
    void Write(string filePath, NormalizedTags tags);
}
