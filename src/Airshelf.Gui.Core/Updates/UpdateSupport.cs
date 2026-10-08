namespace Airshelf.Gui.Core.Updates;

/// <summary>
/// バージョン情報ページで更新の項目を出せるか(#157)。出せないときは理由の文言を出す。シェル(Program)が決める。
/// </summary>
public enum UpdateSupport
{
    /// <summary>Velopack で入れた版で、設定も読めた。更新の項目を出す(更新の仕組みを作れなかったときは、理由を出さずに隠す)。</summary>
    Supported,

    /// <summary>Velopack で入れていない版(開発版・portable)。W-510。</summary>
    NotInstalled,

    /// <summary>Velopack で入れた版だが、設定(<c>radirec.json</c>)を読めず、更新の仕組みを作れない(トークンの出どころが無い)。W-511。</summary>
    ConfigUnreadable,
}
