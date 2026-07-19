namespace Radirec.Core.Output;

/// <summary>
/// パスセグメント(放送局/番組フォルダ名やファイル名)を作るときに不正文字を畳む共有ヘルパ。
/// 出力先(<see cref="LocalFolderTarget"/>)とソース仕分け(SourceOrganizer)で同じ規則を使い、
/// フォルダ命名がずれないようにする(#81 のレビュー指摘)。
/// </summary>
public static class FileNameSanitizer
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>不正なパス文字を "_" に畳む。空になったら "_"。</summary>
    public static string Sanitize(string segment)
    {
        var cleaned = string.Join('_', segment.Split(InvalidChars)).Trim();
        return cleaned.Length == 0 ? "_" : cleaned;
    }
}
