namespace Radirec.Core.Model;

/// <summary>
/// 番組のある放送日の 1 回分(論理エンティティ)。正規化解決後の値を持つ。
/// </summary>
public sealed record Episode
{
    /// <summary>同一性キー(正規局・正規番組・放送日)。</summary>
    public required IdentityKey Identity { get; init; }

    /// <summary>放送時刻(JST。取得できない場合 null)。同一性には関与しない属性。</summary>
    public TimeOnly? BroadcastTime { get; init; }

    /// <summary>出演者(元タグ由来。無ければ null)。</summary>
    public string? Artist { get; init; }

    /// <summary>録音種別(例 TimeFree。無ければ null)。</summary>
    public string? RecordingKind { get; init; }

    /// <summary>放送局または番組名を alias で解決できず生名にフォールバックしたか。</summary>
    public bool UsedFallback { get; init; }

    /// <summary>複数フォルダ統合番組でのパート番号(1 始まり)。単一フォルダなら null。出力名の <c>-N</c> に使う。</summary>
    public int? PartIndex { get; init; }

    public string Station => Identity.CanonicalStation;
    public string Program => Identity.CanonicalProgram;
    public DateOnly BroadcastDate => Identity.BroadcastDate;
}
