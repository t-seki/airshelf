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

        var identity = new IdentityKey(station.Canonical, program.Canonical, source.BroadcastDate);

        return new Episode
        {
            Identity = identity,
            BroadcastTime = source.BroadcastTime,
            Artist = artist,
            RecordingKind = source.RecordingKind,
            UsedFallback = station.UsedFallback || program.UsedFallback,
        };
    }
}
