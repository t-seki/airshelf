using Radirec.Core.Jellyfin;
using Radirec.Core.Ledger;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Output;
using Radirec.Core.Sources;

namespace Radirec.Core.Ingestion;

/// <summary>
/// 取り込みの制御フロー本体。列挙 → 安定判定 → パース → 正規化 → 同一性キー →
/// 冪等判定 → 配置先の算出(同名衝突は音声部分で同じ内容か判定。ADR-0015)→ dry-run 境界 →
/// アトミック非破壊コピー → タグ付与 → 台帳記録(最後)。
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
    IIngestSummaryPublisher summaryPublisher,
    IIngestFailureStore failureStore,
    IIngestFailurePublisher failurePublisher)
{
    /// <summary>中断で残った孤児テンポラリを掃除する(常駐の起動時に呼ぶ。ADR-0003)。</summary>
    public int CleanupOrphans() => target.CleanupOrphans();

    public async Task<IngestReport> RunOnceAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var items = new List<IngestItemResult>();
        foreach (var file in source.Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await ProcessAsync(file, dryRun, cancellationToken));
        }

        var (refreshStatus, refreshError) = await MaybeRefreshLibraryAsync(items, dryRun, cancellationToken);
        var (notified, notifyError) = await MaybePublishSummaryAsync(items, dryRun, cancellationToken);
        var (failNotified, failNotifyError) = await MaybePublishFailuresAsync(items, dryRun, cancellationToken);
        return new IngestReport
        {
            Items = items,
            DryRun = dryRun,
            LibraryRefresh = refreshStatus,
            RefreshError = refreshError,
            NotificationPublished = notified,
            NotificationError = notifyError,
            FailureNotificationPublished = failNotified,
            FailureNotificationError = failNotifyError,
        };
    }

    // 取込失敗シグナルの発行(ADR-0011)。成功要約とは独立に、その走査で**新しく現れた**失敗
    // (de-dup 通過)だけを発行する。dry-run は発行しない。
    private async Task<(bool Published, string? Error)> MaybePublishFailuresAsync(
        IReadOnlyList<IngestItemResult> items, bool dryRun, CancellationToken cancellationToken)
    {
        if (dryRun)
            return (false, null);

        List<NewIngestFailure> newFailures;
        try
        {
            // 成功したファイルの失敗記録を解除する(取込成功で clear。無条件)。一時失敗(ディスク満杯等)が
            // 解消して取り込めたら、将来また失敗したとき再通知できるようにする。
            foreach (var imported in items.Where(i => i.Outcome == IngestOutcome.Imported))
                failureStore.Clear(imported.SourcePath);

            ClearFailuresMissingFromSource(items);

            // 新しく現れた失敗だけを抽出(de-dup)。記録は全件、表示は有界(IngestFailureSignal.From)。
            newFailures = items
                .Where(i => i.Outcome == IngestOutcome.Failed)
                .Select(i => new NewIngestFailure
                {
                    SourcePath = i.SourcePath,
                    Fingerprint = i.Fingerprint,
                    Reason = SanitizeReason(i.Message, i.SourcePath),
                })
                .Where(f => !failureStore.IsKnown(f.SourcePath, f.Fingerprint))
                .ToList();

            if (newFailures.Count == 0)
                return (false, null);

            await failurePublisher.PublishAsync(IngestFailureSignal.From(newFailures), cancellationToken);
        }
        // 通知側(ストアの読取/クリア・publisher の発行)のどの失敗でも取り込み成功は巻き戻さない。握って報告する
        // (通知欠落で成功走査を「失敗」に化けさせない)。発行が投げた場合は記録が進まない = 次走査で再試行
        // (at-least-once。発行前に記録すると書込失敗した失敗を「通知済み」と誤認して永久に見逃す)。
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, ex.Message);
        }

        // 発行成功後にだけ de-dup 記録を進める。記録は表示上限に関わらず**新規全件**(溢れた分も記録しないと
        // 毎走査で再通知される)。ここでの記録失敗は publish 成功を覆さない — 最悪、次走査で重複した失敗シグナルが出るが、
        // 通知自体は届いている(取りこぼしより重複を選ぶ)。
        try
        {
            var failedAt = clock.GetUtcNow();
            foreach (var failure in newFailures)
                failureStore.Record(failure.SourcePath, failure.Fingerprint, failure.Reason, failedAt);
        }
        catch
        {
            // 記録失敗は握る(重複通知はしうるが、成功発行を失敗扱いにしない)。
        }

        return (true, null);
    }

    // 取込元から消えた録音ファイルの失敗記録を消す(#172)。消えたファイルは「原因が解消した」扱いにして、要対応に
    // 残し続けない(表示のたびに File.Exists で確かめない)。この走査で列挙したパスに無い記録だけを消す。
    // 列挙が例外で終わった走査はここまで来ない(RunOnceAsync ごと失敗する)。列挙が 0 件の走査でも消さない
    // (取込元が一時的に見えない〈NAS に届かない・フォルダが無い〉ときに、記録をまとめて消さないため)。
    private void ClearFailuresMissingFromSource(IReadOnlyList<IngestItemResult> items)
    {
        if (items.Count == 0)
            return;

        // 本番(Windows)のパスは大文字小文字を区別しない。区別して比べると、同じファイルの記録を消してしまいうる。
        var enumerated = new HashSet<string>(items.Select(i => i.SourcePath), StringComparer.OrdinalIgnoreCase);
        foreach (var failure in failureStore.List())
        {
            if (!enumerated.Contains(failure.SourcePath))
                failureStore.Clear(failure.SourcePath);
        }
    }

    // 失敗理由(例外メッセージ)を提示向けに整える。例外メッセージにフルパスが埋まっている場合があり
    // (IO 例外等)、フルパスをトーストに晒さない契約(ADR-0011)を守るためファイル名へ畳む。
    private static string SanitizeReason(string? message, string sourcePath)
    {
        var reason = string.IsNullOrWhiteSpace(message) ? "取り込みに失敗しました" : message;
        var fileName = sourcePath[(sourcePath.LastIndexOfAny(['/', '\\']) + 1)..];
        return fileName.Length < sourcePath.Length ? reason.Replace(sourcePath, fileName) : reason;
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
        // 真のキャンセルは伝播、それ以外(publisher の発行失敗等)は握って報告する。
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, ex.Message);
        }
    }

    // 取込が1件以上あった実行のみライブラリ更新を起動(dry-run は起動しない)。
    // 更新失敗で取り込み成功は巻き戻さない(コピーは完了済み)。
    private async Task<(LibraryRefreshStatus Status, string? Error)> MaybeRefreshLibraryAsync(
        IReadOnlyList<IngestItemResult> items, bool dryRun, CancellationToken cancellationToken)
    {
        if (dryRun || !items.Any(i => i.Outcome == IngestOutcome.Imported))
            return (LibraryRefreshStatus.NotNeeded, null);

        return await LibraryRefresh.RunAsync(refresher, cancellationToken);
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

        // import-once: 同一性キー(+放送時刻+パート序数)/ 同一ソースパス / 同一内容(フィンガープリント
        // + 放送日時)のいずれかが既にあればスキップ。最後の content ゲートは、ソースフォルダのリネームや
        // 台帳喪失で前2つが外れても内容同一を捕捉する(#79 / ADR-0014)。同日でも時刻 or part が異なる
        // 別放送は取り込む(ADR-0002 / 0004、厳密一致)。
        if (ledger.Contains(episode.Identity, episode.BroadcastTime, episode.PartIndex ?? 0)
            || ledger.ContainsSource(file.FilePath)
            || ledger.ContainsContent(FileFingerprint.Of(file), episode.BroadcastDate, episode.BroadcastTime))
            return Result(IngestOutcome.SkippedAlreadyImported, file, episode);

        // 未取込のときだけ出演者タグを読む。radikool は識別時に取得済み(episode.Artist)なので再読しない。
        episode = episode with { Artist = episode.Artist ?? SafeReadArtist(file.FilePath) };

        var tags = TagMapper.Map(episode, genre);

        try
        {
            var plan = target.Plan(file, episode, tags);

            // 出力先に同じ内容のファイルが既にある(台帳喪失・size/mtime の変わったコピーで上の 3 関門を
            // すり抜けた回)。書かずに取込済みとし、台帳にも記録して自己修復する(ADR-0015)。
            if (plan.AlreadyPresent)
            {
                if (!dryRun)
                {
                    ledger.Record(Entry(episode, file, plan.Path));
                }
                return Result(IngestOutcome.SkippedAlreadyImported, file, episode, plan.Path);
            }

            // 内容の違う同名ファイルを避けて別名にした。正当な同日・別時刻(ADR-0002)でも起きるので
            // 通知はせず、結果に警告として残す(常駐はログへ Warning で書く)。
            var warning = plan.CollidedWith is { } other
                ? $"出力先に内容の違う同名ファイル({Path.GetFileName(other)})があるため別名で配置します"
                : null;

            if (dryRun)
                return Result(IngestOutcome.Planned, file, episode, plan.Path, episode.UsedFallback, warning);

            var outputPath = await target.WriteAsync(file, plan, tags, cancellationToken);

            ledger.Record(Entry(episode, file, outputPath));

            return Result(IngestOutcome.Imported, file, episode, outputPath, episode.UsedFallback, warning);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 1 ファイルの配置判定/書き込み/タグ/台帳失敗で走査全体を止めない。当該ファイルのみ失敗扱い。
            return Result(IngestOutcome.Failed, file, episode, message: ex.Message);
        }
    }

    private LedgerEntry Entry(Episode episode, RecordingFile file, string outputPath) => new()
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
        Fingerprint = FileFingerprint.Of(file),
    };

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
            // 処理時に(列挙で取得済みの)フィンガープリントを捉える。失敗通知の de-dup は
            // 走査後に SourcePath だけでは内容変化を判定できないため、ここで race-free に持たせる。
            Fingerprint = FileFingerprint.Of(file),
        };
}
