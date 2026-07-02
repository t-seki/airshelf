namespace Radirec.Core.Sources;

/// <summary>
/// 録音ファイルからソース情報を抽出できなかったことを表す例外。
/// 呼び出し側は当該ファイルをスキップ + エラーログとして扱う。
/// </summary>
public sealed class SourceParseException(string message) : Exception(message);
