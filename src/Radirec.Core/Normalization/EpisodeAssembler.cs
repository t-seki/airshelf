using Radirec.Core.Model;

namespace Radirec.Core.Normalization;

/// <summary>
/// 生のソース情報(<see cref="ParsedSource"/>)を正規化解決して放送回(<see cref="Episode"/>)を組む。
/// 出演者はファイル名に無いため、別途タグから読んだ値を渡す。
/// </summary>
public sealed class EpisodeAssembler(IStationResolver stationResolver, IProgramResolver programResolver)
{
    public Episode Assemble(ParsedSource source, string? artist = null)
    {
        var station = stationResolver.Resolve(source.RawStation);
        var program = programResolver.Resolve(station.Canonical, source.RawProgram);

        // 差し替え放送(ADR-0021)。「別の番組として取り込む」回は、フォルダの番組ではなく判断のアルバムにする
        // (局はそのまま。統合番組のパートにはならない)。ほかの扱いはアルバムを変えない(保留・取り込まないは呼び手が見る)。
        var judgement = programResolver.JudgeFilenameProgram(station.Canonical, source.RawProgram, source.FilenameProgram);
        if (judgement is { Verdict: FilenameProgramVerdict.OtherProgram, Album: { } otherAlbum })
            program = new ProgramResolution(otherAlbum, UsedFallback: false);

        var identity = new IdentityKey(station.Canonical, program.Canonical, source.BroadcastDate);

        return new Episode
        {
            Identity = identity,
            BroadcastTime = source.BroadcastTime,
            // radikool は識別時にタグから artist を得ている。無ければ引数(らじれこの遅延読取)。
            Artist = source.Artist ?? artist,
            RecordingKind = source.RecordingKind,
            UsedFallback = station.UsedFallback || program.UsedFallback,
            // ファイル名由来の part(radikool の (N))を優先。無ければ統合フォルダ由来(らじれこ)。
            PartIndex = source.PartIndex ?? program.PartIndex,
            FilenameProgram = source.FilenameProgram,
            FilenameVerdict = judgement.Verdict,
        };
    }
}
