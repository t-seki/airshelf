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
    /// <param name="dryRun">
    /// true なら台帳を副作用なく扱う(既存 DB は読み取り専用で開き、無ければ NullLedger)。
    /// </param>
    /// <returns>サービスと、破棄すべきリソース(台帳。NullLedger 時は null)。</returns>
    public static (IngestService Service, IDisposable? Disposable) Build(RadirecConfig config, bool dryRun)
    {
        var fileSystem = new FileSystem();

        var stations = ConfigLoader.LoadOptional<StationMasterConfig>(config.StationsFile);
        var programs = ConfigLoader.LoadOptional<ProgramMasterConfig>(config.ProgramsFile);

        var assembler = new EpisodeAssembler(
            new StationResolver(stations),
            new ProgramResolver(programs));

        var (ledger, disposable) = BuildLedger(config.LedgerPath, dryRun);

        // ジャンルの優先順位: programs.json の Defaults.Genre(明示時)> radirec.json の Genre > 既定。
        var genre = string.IsNullOrWhiteSpace(programs.Defaults.Genre)
            ? config.Genre
            : programs.Defaults.Genre;
        if (string.IsNullOrWhiteSpace(genre))
            genre = TagMapper.DefaultGenre;

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

        return (service, disposable);
    }

    private static (ILedger Ledger, IDisposable? Disposable) BuildLedger(string ledgerPath, bool dryRun)
    {
        if (!dryRun)
        {
            var writable = new SqliteLedger(ledgerPath);
            return (writable, writable);
        }

        // dry-run: 副作用を避ける。既存台帳は読み取り専用、無ければ NullLedger。
        if (File.Exists(ledgerPath))
        {
            var readOnly = new SqliteLedger(ledgerPath, readOnly: true);
            return (readOnly, readOnly);
        }

        return (new NullLedger(), null);
    }
}
