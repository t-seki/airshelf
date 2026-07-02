using System.IO.Abstractions;
using Radirec.Cli.Configuration;
using Radirec.Core.Configuration;
using Radirec.Core.Ingestion;
using Radirec.Core.Ledger;
using Radirec.Core.Normalization;
using Radirec.Core.Output;
using Radirec.Core.Sources;

namespace Radirec.Cli;

/// <summary>設定から取り込みサービス一式を組み立てる合成ルート(M1 は手配線)。</summary>
public static class CompositionRoot
{
    public static (IngestService Service, SqliteLedger Ledger) Build(RadirecConfig config)
    {
        var fileSystem = new FileSystem();

        var stations = ConfigLoader.LoadOptional<StationMasterConfig>(config.StationsFile);
        var programs = ConfigLoader.LoadOptional<ProgramMasterConfig>(config.ProgramsFile);

        var assembler = new EpisodeAssembler(
            new StationResolver(stations),
            new ProgramResolver(programs));

        var ledger = new SqliteLedger(config.LedgerPath);

        var genre = string.IsNullOrWhiteSpace(programs.Defaults.Genre)
            ? config.Genre
            : programs.Defaults.Genre;

        var service = new IngestService(
            new FileSystemRecordingSource(fileSystem, config.RadioRoot),
            new RajirekoImporter(config.KnownRecordingKinds),
            new StabilityChecker(TimeProvider.System, TimeSpan.FromSeconds(config.StabilitySeconds)),
            new RecordingSkipPolicy(config.SkipPatterns),
            new TagLibTagReader(),
            assembler,
            new LocalFolderTarget(fileSystem, new TagLibTagWriter(), config.OutputRoot),
            ledger,
            TimeProvider.System,
            genre);

        return (service, ledger);
    }
}
