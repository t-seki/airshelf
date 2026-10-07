namespace Airshelf.Core.Model;

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

    /// <summary>
    /// ファイル名の番組名(CONTEXT.md)。らじれこのファイル名から放送局の <c>[...]</c>・録音種別・放送日時を除いた部分。
    /// 番組の同一性には使わず、差し替え放送の疑いを見る手がかりにだけ使う(ADR-0021)。らじれこ以外は null。
    /// </summary>
    public string? FilenameProgram { get; init; }

    /// <summary>放送日(JST の日単位)。同一性キーの構成要素。</summary>
    public required DateOnly BroadcastDate { get; init; }

    /// <summary>放送時刻(JST。取得できない場合 null)。同一性キーには含めない属性。</summary>
    public TimeOnly? BroadcastTime { get; init; }

    /// <summary>録音種別マーカー(例 <c>TimeFree</c>)。無ければ null。</summary>
    public string? RecordingKind { get; init; }

    /// <summary>放送局をフォルダ名から採ったか(ファイル名に <c>[...]</c> が無い場合 true)。</summary>
    public bool StationFromFolder { get; init; }

    /// <summary>
    /// 同日重複の判別子(パート序数)。時刻を持たないソース(radikool)がファイル名の重複マーカー
    /// <c>(N)</c> から採る(マーカー無し = null)。時刻ありソース(らじれこ)は null のまま
    /// (統合フォルダ由来の part は正規化解決側で決まる)。ADR-0004。
    /// </summary>
    public int? PartIndex { get; init; }

    /// <summary>
    /// 出演者(タグ由来で識別前に得られた場合)。radikool は局=genre タグを読む際に同時取得する。
    /// らじれこは null(識別後に遅延読取する。ADR-0003 の性能最適化)。
    /// </summary>
    public string? Artist { get; init; }
}
