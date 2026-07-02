using Radirec.Core.Ledger;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Output;
using Radirec.Core.Sources;

namespace Radirec.Core.Ingestion;

/// <summary>
/// 取り込みの制御フロー本体。列挙 → 安定判定 → パース → 正規化 → 同一性キー →
/// 冪等判定 → dry-run 境界 → アトミック非破壊コピー → タグ付与 → 台帳記録(最後)。
/// </summary>
public sealed class IngestService(
    IRecordingSource source,
    ISourceImporter importer,
    IStabilityChecker stability,
    RecordingSkipPolicy skipPolicy,
    ITagReader tagReader,
    EpisodeAssembler assembler,
    IOutputTarget target,
    ILedger ledger,
    TimeProvider clock,
    string genre)
{
    public async Task<IngestReport> RunOnceAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var items = new List<IngestItemResult>();
        foreach (var file in source.Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await ProcessAsync(file, dryRun, cancellationToken));
        }

        return new IngestReport { Items = items, DryRun = dryRun };
    }

    private async Task<IngestItemResult> ProcessAsync(
        RecordingFile file, bool dryRun, CancellationToken cancellationToken)
    {
        if (skipPolicy.ShouldSkip(file))
            return Result(IngestOutcome.SkippedJunk, file);

        if (!importer.CanHandle(file))
            return Result(IngestOutcome.SkippedUnsupported, file);

        if (!stability.IsStable(file))
            return Result(IngestOutcome.SkippedUnstable, file);

        ParsedSource parsed;
        try
        {
            parsed = importer.Parse(file);
        }
        catch (SourceParseException ex)
        {
            return Result(IngestOutcome.Failed, file, message: ex.Message);
        }

        var artist = SafeReadArtist(file.FilePath);
        var episode = assembler.Assemble(parsed, artist);

        // import-once: 同一性キー または 同一ソースパスが既にあればスキップ。
        if (ledger.Contains(episode.Identity) || ledger.ContainsSource(file.FilePath))
            return Result(IngestOutcome.SkippedAlreadyImported, file, episode);

        var tags = TagMapper.Map(episode, genre);

        if (dryRun)
        {
            var plannedPath = target.PlanPath(file, episode, tags);
            return Result(IngestOutcome.Planned, file, episode, plannedPath, episode.UsedFallback);
        }

        var outputPath = await target.WriteAsync(file, episode, tags, cancellationToken);

        ledger.Record(new LedgerEntry
        {
            IdentityKey = episode.Identity.Value,
            Station = episode.Station,
            Program = episode.Program,
            BroadcastDate = episode.BroadcastDate,
            SourcePath = file.FilePath,
            OutputPath = outputPath,
            RuleVersion = NormalizationRuleVersion.Current,
            UsedFallback = episode.UsedFallback,
            ImportedAtUtc = clock.GetUtcNow(),
        });

        return Result(IngestOutcome.Imported, file, episode, outputPath, episode.UsedFallback);
    }

    private string? SafeReadArtist(string filePath)
    {
        try
        {
            return tagReader.ReadArtist(filePath);
        }
        catch
        {
            // 出演者読取の失敗は取り込みをブロックしない(空で継続)。
            return null;
        }
    }

    private static IngestItemResult Result(
        IngestOutcome outcome, RecordingFile file, Episode? episode = null,
        string? outputPath = null, bool usedFallback = false, string? message = null)
        => new()
        {
            Outcome = outcome,
            SourcePath = file.FilePath,
            Episode = episode,
            OutputPath = outputPath,
            UsedFallback = usedFallback,
            Message = message,
        };
}
