using System.Text.Json.Serialization;

namespace Radirec.Core.Configuration;

/// <summary>番組 alias(programs.json)。表記ゆれ・複数フォルダ → 1 正規番組。</summary>
public sealed class ProgramMasterConfig
{
    public ProgramDefaults Defaults { get; set; } = new();
    public List<ProgramAlias> Programs { get; set; } = [];

    /// <summary>
    /// 分けておく兄弟フォルダ(CONTEXT.md)。統合番組にまとめないと利用者が決めたフォルダの組(ADR-0017 / ADR-0018)。
    /// 取り込みと再適用の解決には使わない(読むのは、要対応と再適用の確認に出す「統合設定の無い兄弟フォルダ」の計算だけ)。
    /// 空なら programs.json に書かない。
    /// </summary>
    public List<KeptApartSiblings> KeepSeparate { get; set; } = [];

    /// <summary>
    /// ファイル名の番組名の判断(CONTEXT.md「差し替え放送」。ADR-0021)。(局, フォルダ, ファイル名の番組名) の組ごとに、
    /// 利用者が決めた扱い。ファイル名の番組名がフォルダ名を含まない回は、ここに判断が無ければ取り込まずに保留する。
    /// 空なら programs.json に書かない。
    /// </summary>
    public List<FilenameProgramDecision> FilenamePrograms { get; set; } = [];
}

/// <summary>
/// ファイル名の番組名の判断 1 件。<c>{ "Match": { "Station", "Folder", "FilenameProgram" }, "Treat", "Album", "Note" }</c>(ADR-0021)。
/// </summary>
public sealed class FilenameProgramDecision
{
    /// <summary>判断する組(局・番組フォルダ・ファイル名の番組名)。</summary>
    public FilenameProgramMatch Match { get; set; } = new();

    /// <summary>扱い。書き忘れを「この番組の回」と読まないよう、省略できない。</summary>
    [JsonRequired]
    public FilenameProgramTreatment Treat { get; set; }

    /// <summary>
    /// 「別の番組として取り込む」(<see cref="FilenameProgramTreatment.OtherProgram"/>)ときのアルバム名。ほかの扱いでは使わない。
    /// null なら programs.json に書かない。
    /// </summary>
    public string? Album { get; set; }

    /// <summary>利用者のメモ。解決には使わない。null なら programs.json に書かない。</summary>
    public string? Note { get; set; }
}

/// <summary>ファイル名の番組名の判断を当てる組。照合は照合キー(<c>TextNormalizer.ToMatchKey</c>)で行う。</summary>
public sealed class FilenameProgramMatch
{
    /// <summary>対象放送局(正規表示名)。</summary>
    public string Station { get; set; } = string.Empty;

    /// <summary>らじれこの番組フォルダ名(1 つ)。</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>ファイル名の番組名(CONTEXT.md)。</summary>
    public string FilenameProgram { get; set; } = string.Empty;
}

/// <summary>ファイル名の番組名の扱い(ADR-0021 の 3 択)。programs.json には名前(<c>"Skip"</c> など)で書く。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FilenameProgramTreatment>))]
public enum FilenameProgramTreatment
{
    /// <summary>この番組の回(フォルダのとおり取り込む)。表記ゆれ・改名・スポンサーの交代・ローテーションゲストなど。</summary>
    SameProgram,

    /// <summary>取り込まない。</summary>
    Skip,

    /// <summary>
    /// 別の番組として取り込む(アルバム名は <see cref="FilenameProgramDecision.Album"/>。局はそのまま。統合番組のフォルダから来た回は
    /// そのフォルダのパートを引き継ぐ。#239)。
    /// </summary>
    OtherProgram,
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
