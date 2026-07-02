namespace Radirec.Core.Configuration;

/// <summary>番組 alias(programs.json)。表記ゆれ・複数フォルダ → 1 正規番組。</summary>
public sealed class ProgramMasterConfig
{
    public ProgramDefaults Defaults { get; set; } = new();
    public List<ProgramAlias> Programs { get; set; } = [];
}

public sealed class ProgramDefaults
{
    /// <summary>既定ジャンル。</summary>
    public string Genre { get; set; } = "ラジオ";
}

public sealed class ProgramAlias
{
    public ProgramMatch Match { get; set; } = new();
    public ProgramCanonical Canonical { get; set; } = new();
}

public sealed class ProgramMatch
{
    /// <summary>対象放送局(正規表示名)。</summary>
    public string Station { get; set; } = string.Empty;

    /// <summary>照合する生番組名 / 正規化フォルダ名(複数可 = 手動マージ)。</summary>
    public List<string> Keys { get; set; } = [];
}

public sealed class ProgramCanonical
{
    /// <summary>正規番組名(= アルバム名)。</summary>
    public string Album { get; set; } = string.Empty;
}
