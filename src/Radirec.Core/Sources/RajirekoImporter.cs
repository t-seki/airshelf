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

    // らじれこは通常 M4A だが一部の局/設定で MP3 も出す。命名(末尾 14桁タイムスタンプ)が唯一のアンカーで
    // 音声コンテナには依存しないため .m4a / .mp3 の両方を受理する。radikool の MP3 は 8桁日付で
    // TimestampSuffix(14桁)に当たらないため、ここでは掴まない(SourceRouter が RadikoolImporter へ回す)。
    public bool CanHandle(RecordingFile file)
        => (file.Extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
           && TimestampSuffix.IsMatch(file.FileName);

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
