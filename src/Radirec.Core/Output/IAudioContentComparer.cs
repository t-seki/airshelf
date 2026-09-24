namespace Radirec.Core.Output;

/// <summary>
/// 2 つの録音ファイルが「タグ以外の音声部分」で同じ内容かを判定する。出力先の同名衝突で、既存ファイルが
/// 同じ放送回の取込済みコピーか、別の放送かを見分けるために使う(ADR-0015)。
/// </summary>
public interface IAudioContentComparer
{
    /// <summary>
    /// 音声部分が同じなら true。判定できないとき(読めない・形式を解釈できない・音声区間が取れない)は
    /// 例外を投げず false を返す — 「違う内容」として書き込ませ、欠落させない(欠落 > 重複。ADR-0015)。
    /// </summary>
    bool HasSameAudio(string pathA, string pathB);
}
