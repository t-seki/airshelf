using Radirec.Core.Model;

namespace Radirec.Core.Sources;

/// <summary>
/// 録音元ツール(らじれこ / radikool 等)ごとに差し替えるパーサ。
/// ファイル名・フォルダ構造から生の放送情報(<see cref="ParsedSource"/>)を抽出する。
/// </summary>
public interface ISourceImporter
{
    /// <summary>このファイルを当該ソースとして扱えるか。</summary>
    bool CanHandle(RecordingFile file);

    /// <summary>録音ファイルを解析して生のソース情報を返す。失敗時は <see cref="SourceParseException"/>。</summary>
    ParsedSource Parse(RecordingFile file);
}
