namespace Radirec.Core.Model;

/// <summary>
/// ファイル名の番組名(CONTEXT.md)から見た、放送回の扱い(ADR-0021)。フォルダ名(か番組のアルバム名)を含むか、利用者の判断があるか、どちらも無い(保留)か。
/// </summary>
public enum FilenameProgramVerdict
{
    /// <summary>
    /// ファイル名の番組名がフォルダ名を含む(照合キーで比べる)。フォルダが属する正規名の設定のアルバム名(統合番組ならそのアルバム名)を
    /// 含む場合もこれにする(#241)。これまでどおりフォルダの番組として取り込む。
    /// ファイル名の番組名を持たない回(radikool・名前が空)もこれにする(疑う手がかりが無い)。
    /// </summary>
    ContainsFolder,

    /// <summary>判断「この番組の回」。フォルダの番組として取り込む。</summary>
    SameProgram,

    /// <summary>判断「取り込まない」。</summary>
    Skip,

    /// <summary>判断「別の番組として取り込む」。アルバムは判断のアルバム名(<see cref="Episode.Program"/> に入る)。</summary>
    OtherProgram,

    /// <summary>フォルダ名もアルバム名も含まず、判断も無い(差し替え放送の疑い)。取り込まずに保留する。</summary>
    Unjudged,
}
