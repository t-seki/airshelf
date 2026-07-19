using System.Globalization;
using System.Text.RegularExpressions;
using Radirec.Core.Model;

namespace Radirec.Core.Sources;

/// <summary>
/// らじれこ出力(既定は M4A。一部の局/設定で MP3 も出る)の importer。<b>末尾タイムスタンプ
/// <c>_{YYYYMMDDHHMMSS}</c>(14桁)を唯一の普遍アンカー</b>にして、ファイル名から
/// <b>放送局・録音種別・放送日時のみ</b>を抽出する
/// (実データ 2176 件で検証した 3 形状に対応):
/// <list type="bullet">
///   <item>A: <c>[局]番組(TimeFree)_ts.m4a</c>(標準)</item>
///   <item>B: <c>[局]番組_ts.m4a</c>(録音種別マーカー無し)</item>
///   <item>C: <c>番組_ts.m4a</c>(局プレフィックス無し。局はフォルダ名を権威にする)</item>
/// </list>
/// <b>番組の同一性(アルバム)は常にフォルダ名を拠り所にする</b>(ADR-0001)。ファイル名の
/// 番組部は週替わりゲスト等で変動するため識別には使わない。録音種別マーカーは既知許可リストに
/// 一致する末尾のものだけ剥がす(番組名やフォルダ名が含む括弧を誤って切り落とさないため)。
/// </summary>
public sealed class RajirekoImporter : ISourceImporter
{
    private static readonly Regex TimestampSuffix =
        new(@"_(?<ts>[0-9]{14})\.[^.]+$", RegexOptions.Compiled);

    private readonly IReadOnlyList<string> _knownRecordingKinds;

    /// <param name="knownRecordingKinds">
    /// 剥がしてよい録音種別マーカーの許可リスト(既定 <c>TimeFree</c>)。config で拡張可能。
    /// </param>
    public RajirekoImporter(IEnumerable<string>? knownRecordingKinds = null)
        => _knownRecordingKinds = (knownRecordingKinds ?? ["TimeFree"]).ToArray();

    public bool CanHandle(RecordingFile file)
    {
        if (!TimestampSuffix.IsMatch(file.FileName))
            return false;

        // M4A は らじれこ専用フォーマットで radikool(常に MP3)と衝突しない → 全形状(A/B/C)を受理。
        if (file.Extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
            return true;

        // MP3 は radikool と共有する。らじれこも一部の局/設定で MP3 を出すが、**閉じた放送局ブラケット
        // [局] がある**もの(形状 A/B)だけ受理する。ブラケット無しの裸 MP3(radikool 由来、または radikool の
        // 8桁が _…000000 の 14桁へ変換された等)を Rajireko semantics(番組=フォルダ・局=フォルダ・
        // 時刻=00:00)で無音のまま誤取込しないための保険。裸 MP3 は対象外に留めて可視化し、人手判断に委ねる。
        // 開き括弧のみの壊れ名は Parse が ']' 欠如で throw(=Failed)するため、ここで閉じ括弧も要求して
        // 対象外(可視・非 Failed)に留める。
        return file.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
               && file.FileName.StartsWith('[')
               && file.FileName.Contains(']');
    }

    public ParsedSource Parse(RecordingFile file)
    {
        var name = file.FileName;

        var match = TimestampSuffix.Match(name);
        if (!match.Success)
            throw new SourceParseException($"末尾タイムスタンプが見つかりません: {name}");

        var ts = match.Groups["ts"].Value;
        if (!DateTime.TryParseExact(
                ts, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var broadcast))
        {
            throw new SourceParseException($"放送日時が不正です ({ts}): {name}");
        }

        var date = DateOnly.FromDateTime(broadcast);
        var time = TimeOnly.FromDateTime(broadcast);

        // ファイル名から末尾 "_{14桁}.ext" を除いた部分。
        var stem = name[..match.Index];

        string rawStation;
        string programPart;
        bool stationFromFolder;

        if (stem.StartsWith('['))
        {
            var close = stem.IndexOf(']');
            if (close < 0)
                throw new SourceParseException($"放送局括弧 ']' が閉じていません: {name}");

            rawStation = stem[1..close];
            programPart = stem[(close + 1)..];
            stationFromFolder = false;
        }
        else
        {
            // 局プレフィックス無し → フォルダを権威にする。
            rawStation = file.StationFolder;
            programPart = stem;
            stationFromFolder = true;
        }

        // 録音種別マーカーはファイル名の番組部末尾から採る(番組名の識別には使わない)。
        var kind = StripTrailingRecordingKind(ref programPart);

        // 番組の同一性はフォルダ名を拠り所にする(ADR-0001)。ファイル名は週替わりゲスト等で
        // 変動するため、識別には使わない。
        return new ParsedSource
        {
            RawStation = rawStation.Trim(),
            RawProgram = file.ProgramFolder.Trim(),
            BroadcastDate = date,
            BroadcastTime = time,
            RecordingKind = kind,
            StationFromFolder = stationFromFolder,
        };
    }

    /// <summary>
    /// 番組部の末尾が既知マーカー <c>(Kind)</c> なら剥がして種別を返す。無ければ null。
    /// 任意の末尾括弧は剥がさない(番組名の括弧を守る)。
    /// </summary>
    private string? StripTrailingRecordingKind(ref string programPart)
    {
        var trimmed = programPart.TrimEnd();
        foreach (var kind in _knownRecordingKinds)
        {
            var marker = $"({kind})";
            if (trimmed.EndsWith(marker, StringComparison.Ordinal))
            {
                programPart = trimmed[..^marker.Length];
                return kind;
            }
        }

        return null;
    }
}
