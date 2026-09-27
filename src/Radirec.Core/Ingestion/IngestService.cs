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
/// 差し替え放送の疑いがある回(ファイル名の番組名がフォルダ名を含まず、判断も無い)は取り込まずに保留し、
/// 「取り込まない」と判断された回は取り込まない(ADR-0021)。
/// </summary>
/// <param name="heldStore">保留のストア(ADR-0021)。省略すると記録しない(取込失敗のストアと同じく、発行する経路でだけ実体を渡す)。</param>
/// <param name="heldPublisher">保留シグナルの publisher。省略すると発行しない。</param>
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
    IIngestFailurePublisher failurePublisher,
    IHeldRecordingStore? heldStore = null,
    IIngestHeldPublisher? heldPublisher = null)
{
    private readonly IHeldRecordingStore _heldStore = heldStore ?? new NullHeldRecordingStore();
    private readonly IIngestHeldPublisher _heldPublisher = heldPublisher ?? new NullIngestHeldPublisher();

    /// <summary>中断で残った孤児テンポラリを掃除する(常駐の起動時に呼ぶ。ADR-0003)。</summary>
    public int CleanupOrphans() => target.CleanupOrphans();

    /// <exception cref="IngestCanceledException">
    /// <paramref name="cancellationToken"/> の取り消しで打ち切った。それまでに処理した明細を持つ(呼び手が件数を残せるように。#218)。
    /// </exception>
    public async Task<IngestReport> RunOnceAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var items = new List<IngestItemResult>();
        try
        {
            return await RunOnceCoreAsync(items, dryRun, cancellationToken);
        }
        catch (OperationCanceledException ex) when (ex is not IngestCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new IngestCanceledException(items.ToList(), ex, cancellationToken);
        }
    }

    // items には処理し終えたファイルの明細を足していく(打ち切られたときに、呼び手の RunOnceAsync がそれまでの分を返せるように)。
    private async Task<IngestReport> RunOnceCoreAsync(
        List<IngestItemResult> items, bool dryRun, CancellationToken cancellationToken)
    {
        var held = new List<NewHeldRecording>();
        foreach (var file in source.Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await ProcessAsync(file, dryRun, cancellationToken);
            items.Add(item);
            if (item is { Outcome: IngestOutcome.Held, Episode: { } episode })
                held.Add(new NewHeldRecording(file.FilePath, episode.Station, file.ProgramFolder, item.FilenameProgram ?? string.Empty));
        }

        var (refreshStatus, refreshError) = await MaybeRefreshLibraryAsync(items, dryRun, cancellationToken);
        var (notified, notifyError, notifySuppressed) = await MaybePublishSummaryAsync(items, dryRun, cancellationToken);
        var (failNotified, failNotifyError) = await MaybePublishFailuresAsync(items, dryRun, cancellationToken);
        var (heldNotified, heldNotifyError) = await MaybeRecordHeldAsync(items, held, dryRun, cancellationToken);
        return new IngestReport
        {
            Items = items,
            DryRun = dryRun,
            LibraryRefresh = refreshStatus,
            RefreshError = refreshError,
            NotificationPublished = notified,
            NotificationError = notifyError,
            NotificationSuppressedReason = notifySuppressed,
            FailureNotificationPublished = failNotified,
            FailureNotificationError = failNotifyError,
            HeldNotificationPublished = heldNotified,
            HeldNotificationError = heldNotifyError,
        };
    }

    // 保留のストアを走査の結果に合わせ、初めて保留した録音ファイルがあれば保留シグナルを発行する(ADR-0021)。dry-run は何もしない。
    // 取込失敗のストアと違い、**記録してから発行する**。保留のストアは通知の de-dup だけでなく要対応の元でもあるので、発行に失敗しても
    // 記録は残す(要対応には出る。その代わり、そのトーストは出直さない)。
    private async Task<(bool Published, string? Error)> MaybeRecordHeldAsync(
        IReadOnlyList<IngestItemResult> items, IReadOnlyList<NewHeldRecording> held, bool dryRun,
        CancellationToken cancellationToken)
    {
        if (dryRun)
            return (false, null);

        List<NewHeldRecording> newHeld;
        try
        {
            ClearResolvedHeld(items);

            newHeld = held.Where(h => !_heldStore.IsHeld(h.SourcePath)).ToList();
            var heldAt = clock.GetUtcNow();
            foreach (var h in newHeld)
                _heldStore.Record(new HeldRecording(h.SourcePath, h.Station, h.Folder, h.FilenameProgram, heldAt));

            if (newHeld.Count == 0)
                return (false, null);

            await _heldPublisher.PublishAsync(IngestHeldSignal.From(newHeld), cancellationToken);
        }
        // ストアの読み書き・発行のどの失敗でも取り込みは巻き戻さない。握って報告する(失敗シグナルと同じ)。
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, ex.Message);
        }

        return (true, null);
    }

    // もう保留していない録音ファイルの記録を消す。この走査で保留した(Held)・まだ安定していない(SkippedUnstable。判断は
    // 変わっていない)もの以外は、取り込んだ・取り込まないと判断された・既に台帳にあるなどで保留が解けている。列挙に無いものは
    // 取込元から消えた(#172 の取込失敗と同じ。列挙が 0 件の走査では、取込元が一時的に見えないだけかもしれないので消さない)。
    private void ClearResolvedHeld(IReadOnlyList<IngestItemResult> items)
    {
        if (items.Count == 0)
            return;

        // 失敗のストアと同じく、大文字小文字を区別して比べる(ClearFailuresMissingFromSource)。
        var outcomes = new Dictionary<string, IngestOutcome>(StringComparer.Ordinal);
        foreach (var item in items)
            outcomes[item.SourcePath] = item.Outcome;

        foreach (var record in _heldStore.List())
        {
            var stillHeld = outcomes.TryGetValue(record.SourcePath, out var outcome)
                && outcome is IngestOutcome.Held or IngestOutcome.SkippedUnstable;
            if (!stillHeld)
                _heldStore.Clear(record.SourcePath);
        }
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

        // ストア(failures の主キー・IsKnown・Record・Clear)と同じく、大文字小文字を区別して比べる。区別しないと、
        // 走査の間でパスの表記(大文字小文字)が変わったとき、古い表記の行がここでも成功時の Clear でも消えず、
        // 要対応に残り続ける。区別すれば古い表記の行は消え、新しい表記で記録し直される(IsKnown も区別するので、
        // 表記が変われば今でも再通知される)。
        var enumerated = new HashSet<string>(items.Select(i => i.SourcePath), StringComparer.Ordinal);
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
    // refresh の直後に出す。発行失敗で取り込み成功は巻き戻さない(refresh と同方針)。publisher が止めたとき
    // (走査の実行・設定でオフ)は、出したことにせず理由を返す(#215)。
    private async Task<(bool Published, string? Error, string? SuppressedReason)> MaybePublishSummaryAsync(
        IReadOnlyList<IngestItemResult> items, bool dryRun, CancellationToken cancellationToken)
    {
        if (dryRun)
            return (false, null, null);

        var imported = items
            .Where(i => i.Outcome == IngestOutcome.Imported && i.Episode is not null)
            .Select(i => i.Episode!)
            .ToList();
        if (imported.Count == 0)
            return (false, null, null);

        try
        {
            var suppressed = await summaryPublisher.PublishAsync(IngestSummary.From(imported), cancellationToken);
            return (suppressed is null, null, suppressed);
        }
        // 真のキャンセルは伝播、それ以外(publisher の発行失敗等)は握って報告する。
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, ex.Message, null);
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

        // 差し替え放送(ADR-0021)。取り込み済みの回は上で「済」になる(判断待ちの取り込み済みの回は要対応が台帳から見つける)。
        // 未取込の回で、判断の無いものは保留し、「取り込まない」と判断されたものは取り込まない。
        switch (episode.FilenameVerdict)
        {
            case FilenameProgramVerdict.Unjudged:
                return Result(IngestOutcome.Held, file, episode) with { FilenameProgram = episode.FilenameProgram };
            case FilenameProgramVerdict.Skip:
                return Result(IngestOutcome.SkippedByDecision, file, episode) with { FilenameProgram = episode.FilenameProgram };
        }

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
