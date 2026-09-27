using System.Globalization;
using System.Text.RegularExpressions;
using Radirec.Core.Model;

namespace Radirec.Core.Sources;

/// <summary>
/// らじれこ出力(既定は M4A。一部の局/設定で MP3 も出る)の importer。<b>末尾タイムスタンプ
/// <c>_{YYYYMMDDHHMMSS}</c>(14桁)を唯一の普遍アンカー</b>にして、ファイル名から
/// <b>放送局・録音種別・放送日時のみ</b>を抽出する
/// (実データで検証した 3 形状。<c>_ts</c> = 14桁タイムスタンプ):
/// <list type="bullet">
///   <item>A: <c>[局]番組(TimeFree)_ts</c>(標準)</item>
///   <item>B: <c>[局]番組_ts</c>(録音種別マーカー無し)</item>
///   <item>C: <c>番組_ts</c>(局プレフィックス無し。局はフォルダ名を権威にする)</item>
/// </list>
/// <b>拡張子ごとの受理範囲</b>(#84):<c>.m4a</c> は らじれこ専用フォーマットで radikool(常に MP3)と
/// 衝突しないため<b>全形状 A/B/C を受理</b>。<c>.mp3</c> は radikool と共有するため、放送局ブラケット
/// <c>[局]</c> を持つ<b>形状 A/B のみ受理</b>し、ブラケット無しの形状 C の MP3 は受理しない(対象外)。
/// これは radikool 由来 or 8桁日付が <c>_…000000</c> の 14桁へ変換された「裸の MP3」を Rajireko
/// semantics(番組=フォルダ・局=フォルダ・時刻=00:00)で無音のまま誤取込しないための保険。radikool の
/// 実ファイルは <c>[局]</c> を持たないため、このブラケット判定で radikool と らじれこ MP3 を実務上分離できる。
/// <b>番組の同一性(アルバム)は常にフォルダ名を拠り所にする</b>(ADR-0001)。ファイル名の
/// 番組部は週替わりゲスト等で変動するため識別には使わない(差し替え放送の疑いを見る手がかりとして
/// <see cref="ParsedSource.FilenameProgram"/> に載せるだけ。ADR-0021)。録音種別マーカーは既知許可リストに
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
            // 識別には使わない。差し替え放送の疑いを見る手がかり(ADR-0021)。
            FilenameProgram = programPart.Trim(),
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
