using System.Globalization;
using System.Text.RegularExpressions;
using Radirec.Core.Model;
using Radirec.Core.Output;

namespace Radirec.Core.Sources;

/// <summary>
/// radikool 出力(MP3)の importer。らじれこと権威が逆で、<b>番組名はファイル名 stem</b>
/// (局コードフォルダしか持たないため)、<b>放送局は ID3 の genre タグ</b>(局コードフォルダは
/// 誤分類があり信用しない。ADR-0004 / CONTEXT)。時刻は持たない。
/// <list type="bullet">
///   <item>ファイル名: <c>{番組名}_{YYYYMMDD}.mp3</c>(標準)</item>
///   <item><c>{番組名}_{YYYYMMDD}(N).mp3</c>(同日重複マーカー。<c>(N)</c> を part 序数にする)</item>
/// </list>
/// 局(genre)と出演者(artist)は識別に先立ってタグから読む(<see cref="ITagReader"/>)。
/// らじれこの「識別後に遅延読取」最適化とは異なる、タグ駆動識別のソース。
/// </summary>
public sealed class RadikoolImporter(ITagReader tagReader) : ISourceImporter
{
    // 末尾 "_YYYYMMDD" と任意の同日重複マーカー "(N)"。
    private static readonly Regex DateSuffix =
        new(@"_(?<date>[0-9]{8})(?:\((?<part>[0-9]+)\))?\.[^.]+$", RegexOptions.Compiled);

    public bool CanHandle(RecordingFile file)
        => file.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
           && DateSuffix.IsMatch(file.FileName);

    public ParsedSource Parse(RecordingFile file)
    {
        var name = file.FileName;

        var match = DateSuffix.Match(name);
        if (!match.Success)
            throw new SourceParseException($"末尾日付が見つかりません: {name}");

        if (!DateOnly.TryParseExact(
                match.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            throw new SourceParseException($"放送日が不正です ({match.Groups["date"].Value}): {name}");
        }

        int? partIndex = match.Groups["part"].Success
            ? int.Parse(match.Groups["part"].Value, CultureInfo.InvariantCulture)
            : null;

        // 番組名 = 末尾 "_YYYYMMDD(N).mp3" を除いた stem。
        var program = name[..match.Index].Trim();

        // 局 = genre タグ(権威)。無ければ局コードフォルダにフォールバック(Q1)。出演者も同時取得。
        var tags = tagReader.ReadTags(file.FilePath);
        var stationFromFolder = string.IsNullOrWhiteSpace(tags.Genre);
        var rawStation = stationFromFolder ? file.StationFolder.Trim() : tags.Genre!.Trim();

        return new ParsedSource
        {
            RawStation = rawStation,
            RawProgram = program,
            BroadcastDate = date,
            BroadcastTime = null, // radikool は時刻を持たない。
            RecordingKind = null,
            StationFromFolder = stationFromFolder,
            PartIndex = partIndex,
            Artist = tags.Artist,
        };
    }
}
