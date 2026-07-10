using Radirec.Core.Jellyfin;
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
    string genre,
    ILibraryRefresher refresher,
    IIngestSummaryPublisher summaryPublisher)
{
    /// <summary>中断で残った孤児テンポラリを掃除する(サービス起動時に呼ぶ。ADR-0003)。</summary>
    public int CleanupOrphans() => target.CleanupOrphans();

    public async Task<IngestReport> RunOnceAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var items = new List<IngestItemResult>();
        foreach (var file in source.Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await ProcessAsync(file, dryRun, cancellationToken));
        }

        var (refreshed, refreshError) = await MaybeRefreshLibraryAsync(items, dryRun, cancellationToken);
        var (notified, notifyError) = await MaybePublishSummaryAsync(items, dryRun, cancellationToken);
        return new IngestReport
        {
            Items = items,
            DryRun = dryRun,
            LibraryRefreshed = refreshed,
            RefreshError = refreshError,
            NotificationPublished = notified,
            NotificationError = notifyError,
        };
    }

    // 取込が1件以上あった実行のみ取込要約シグナルを発行(dry-run は発行しない)。
    // refresh の直後に出す。発行失敗で取り込み成功は巻き戻さない(refresh と同方針)。
    private async Task<(bool Published, string? Error)> MaybePublishSummaryAsync(
        IReadOnlyList<IngestItemResult> items, bool dryRun, CancellationToken cancellationToken)
    {
        if (dryRun)
            return (false, null);

        var imported = items
            .Where(i => i.Outcome == IngestOutcome.Imported && i.Episode is not null)
            .Select(i => i.Episode!)
            .ToList();
        if (imported.Count == 0)
            return (false, null);

        try
        {
            await summaryPublisher.PublishAsync(IngestSummary.From(imported), cancellationToken);
            return (true, null);
        }
        // 真のキャンセルは伝播、それ以外(EventLog 書込失敗等)は握って報告する。
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, ex.Message);
        }
    }

    // 取込が1件以上あった実行のみライブラリ更新を起動(dry-run は起動しない)。
    // 更新失敗で取り込み成功は巻き戻さない(コピーは完了済み)。
    private async Task<(bool Refreshed, string? Error)> MaybeRefreshLibraryAsync(
        IReadOnlyList<IngestItemResult> items, bool dryRun, CancellationToken cancellationToken)
    {
        if (dryRun || !items.Any(i => i.Outcome == IngestOutcome.Imported))
            return (false, null);

        try
        {
            await refresher.RefreshAsync(cancellationToken);
            return (true, null);
        }
        // 真のキャンセル(呼び出し元のトークン発火)は伝播させる。
        // HttpClient のタイムアウト(トークン未発火の TaskCanceledException)含むそれ以外の
        // 失敗は握って報告する(取り込み成功は巻き戻さない)。
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, ex.Message);
        }
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

        // 同一性(局/番組/日時)は出演者タグ無しで決まる。先に台帳判定し、既取込なら
        // タグを読まずにスキップする(常駐キャッチアップスキャン/dry-run の高速化。ADR-0003)。
        var episode = assembler.Assemble(parsed);

        // import-once: 同一性キー(+放送時刻+パート序数)または 同一ソースパスが既にあればスキップ。
        // 同日でも時刻 or part が異なる別放送は取り込む(ADR-0002 / 0004、厳密一致)。
        if (ledger.Contains(episode.Identity, episode.BroadcastTime, episode.PartIndex ?? 0)
            || ledger.ContainsSource(file.FilePath))
            return Result(IngestOutcome.SkippedAlreadyImported, file, episode);

        // 未取込のときだけ出演者タグを読む。radikool は識別時に取得済み(episode.Artist)なので再読しない。
        episode = episode with { Artist = episode.Artist ?? SafeReadArtist(file.FilePath) };

        var tags = TagMapper.Map(episode, genre);

        if (dryRun)
        {
            var plannedPath = target.PlanPath(file, episode, tags);
            return Result(IngestOutcome.Planned, file, episode, plannedPath, episode.UsedFallback);
        }

        try
        {
            var outputPath = await target.WriteAsync(file, episode, tags, cancellationToken);

            ledger.Record(new LedgerEntry
            {
                IdentityKey = episode.Identity.Value,
                Station = episode.Station,
                Program = episode.Program,
                BroadcastDate = episode.BroadcastDate,
                BroadcastTime = episode.BroadcastTime,
                PartIndex = episode.PartIndex ?? 0,
                SourcePath = file.FilePath,
                OutputPath = outputPath,
                RuleVersion = NormalizationRuleVersion.Current,
                UsedFallback = episode.UsedFallback,
                ImportedAtUtc = clock.GetUtcNow(),
            });

            return Result(IngestOutcome.Imported, file, episode, outputPath, episode.UsedFallback);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 1 ファイルの書き込み/タグ/台帳失敗で走査全体を止めない。当該ファイルのみ失敗扱い。
            return Result(IngestOutcome.Failed, file, episode, message: ex.Message);
        }
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
