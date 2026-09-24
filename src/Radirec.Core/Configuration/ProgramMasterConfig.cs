namespace Radirec.Core.Configuration;

/// <summary>番組 alias(programs.json)。表記ゆれ・複数フォルダ → 1 正規番組。</summary>
public sealed class ProgramMasterConfig
{
    public ProgramDefaults Defaults { get; set; } = new();
    public List<ProgramAlias> Programs { get; set; } = [];

    /// <summary>
    /// 分けておく兄弟フォルダ(CONTEXT.md)。統合番組にまとめないと利用者が決めたフォルダの組(ADR-0017 / ADR-0018)。
    /// 取り込みと再適用の解決には使わない(読むのは要対応の計算だけ)。空なら programs.json に書かない。
    /// </summary>
    public List<KeptApartSiblings> KeepSeparate { get; set; } = [];
}

/// <summary>分けておく兄弟フォルダの 1 組。<c>{ "Match": { "Station", "Folders" }, "Note" }</c>。</summary>
public sealed class KeptApartSiblings
{
    /// <summary>局とフォルダの組(<see cref="ProgramAlias.Match"/> と同じ形)。</summary>
    public ProgramMatch Match { get; set; } = new();

    /// <summary>利用者のメモ(なぜ分けておくか)。null なら programs.json に書かない。</summary>
    public string? Note { get; set; }
}

public sealed class ProgramDefaults
{
    /// <summary>
    /// 既定ジャンル。**空**が既定(未指定)で、明示された場合のみ radirec.json の Genre を上書きする。
    /// programs.json が無い/未指定なら空のままとなり、上位(radirec.json)の値が使われる。
    /// </summary>
    public string Genre { get; set; } = string.Empty;
}

public sealed class ProgramAlias
{
    public ProgramMatch Match { get; set; } = new();
    public ProgramCanonical Canonical { get; set; } = new();

    /// <summary>
    /// 利用者のメモ(なぜ統合したか等。ADR-0017 で // コメントの代わりに持つ)。解決には使わない。
    /// null なら programs.json に書かない。
    /// </summary>
    public string? Note { get; set; }
}

public sealed class ProgramMatch
{
    /// <summary>対象放送局(正規表示名)。</summary>
    public string Station { get; set; } = string.Empty;

    /// <summary>照合する番組フォルダ名(複数可 = 複数フォルダを 1 アルバムへ統合)。</summary>
    public List<string> Folders { get; set; } = [];
}

public sealed class ProgramCanonical
{
    /// <summary>正規番組名(= アルバム名)。</summary>
    public string Album { get; set; } = string.Empty;
}
