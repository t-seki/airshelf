namespace Radirec.Core.Model;

/// <summary>
/// ソース別 importer が録音ファイルから抽出した「生の」情報(正規化前)。
/// 放送局・番組名はまだ表記ゆれを含む生の値。
/// </summary>
public sealed record ParsedSource
{
    /// <summary>生の放送局名(ファイル名の <c>[...]</c> またはフォルダ由来)。</summary>
    public required string RawStation { get; init; }

    /// <summary>生の番組名(ファイル名またはフォルダ由来)。</summary>
    public required string RawProgram { get; init; }

    /// <summary>放送日(JST の日単位)。同一性キーの構成要素。</summary>
    public required DateOnly BroadcastDate { get; init; }

    /// <summary>放送時刻(JST。取得できない場合 null)。同一性キーには含めない属性。</summary>
    public TimeOnly? BroadcastTime { get; init; }

    /// <summary>録音種別マーカー(例 <c>TimeFree</c>)。無ければ null。</summary>
    public string? RecordingKind { get; init; }

    /// <summary>放送局をフォルダ名から採ったか(ファイル名に <c>[...]</c> が無い場合 true)。</summary>
    public bool StationFromFolder { get; init; }

    /// <summary>番組名をフォルダ名から採ったか(ファイル名の構造が壊れている場合 true)。</summary>
    public bool ProgramFromFolder { get; init; }
}
