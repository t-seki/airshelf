using System.IO.Abstractions;
using Radirec.Core.Configuration;
using Radirec.Core.Ingestion;
using Radirec.Core.Jellyfin;
using Radirec.Core.Ledger;
using Radirec.Core.Normalization;
using Radirec.Core.Output;
using Radirec.Core.Reapply;
using Radirec.Core.Sources;

namespace Radirec.Hosting;

/// <summary>設定から取り込みサービス一式を組み立てる合成ルート(M1 は手配線)。</summary>
public static class CompositionRoot
{
    /// <param name="dryRun">
    /// true なら台帳を副作用なく扱う(既存 DB は読み取り専用で開き、無ければ NullLedger)。台帳のフォルダに届かなければ
    /// 「無い」とは扱わず <see cref="LedgerUnreachableException"/> を投げる(#216)。false なら書き込み用に開き、フォルダが無ければ
    /// 作る。開けなければ(作れない・届かない)同じく <see cref="LedgerUnreachableException"/>(<see cref="LedgerFile.OpenWritable"/>。#228)。
    /// </param>
    /// <param name="notify">
    /// 取込通知を発行するか。**常駐経路のみ true**。CLI がここを直接組むのは <c>ingest --dry-run</c> だけで
    /// (非 dry-run の <c>radirec ingest</c> は常駐に頼む。#203)、dry-run はそもそも発行しない(ADR-0012)。
    /// </param>
    /// <param name="summaryPublisher">
    /// 取込要約**シグナル**の publisher を注入する(現行の実装はトレイ常駐の in-process トースト。ADR-0012)。
    /// null なら発行しない(<see cref="SelectPublishers"/> が Null publisher を返す)。
    /// </param>
    /// <param name="failurePublisher">取込失敗**シグナル**の publisher。<paramref name="summaryPublisher"/> と同様。</param>
    /// <param name="heldPublisher">
    /// 保留**シグナル**の publisher(ADR-0021)。保留のストアは取込失敗のストアと同じく発行する経路でだけ使い、そのときこれを
    /// 発行に使う(null なら記録だけして発行しない)。
    /// </param>
    /// <returns>サービスと、破棄すべきリソース(台帳。NullLedger 時は null)。</returns>
    /// <exception cref="ProgramsConfigException">programs.json の判断(<c>FilenamePrograms</c>)が不正(<see cref="ProgramResolver"/>)。</exception>
    public static (IngestService Service, IDisposable? Disposable) Build(
        RadirecConfig config, bool dryRun, bool notify = false,
        IIngestSummaryPublisher? summaryPublisher = null, IIngestFailurePublisher? failurePublisher = null,
        IIngestHeldPublisher? heldPublisher = null)
    {
        var fileSystem = new FileSystem();

        var stations = ConfigLoader.LoadOptional<StationMasterConfig>(config.StationsFile);
        var programs = ConfigLoader.LoadOptional<ProgramMasterConfig>(config.ProgramsFile);

        var programResolver = new ProgramResolver(programs);
        var assembler = new EpisodeAssembler(
            new StationResolver(stations),
            programResolver);

        var (ledger, failureStoreFacet, heldStoreFacet, ledgerDisposable) = BuildLedger(config.LedgerPath, dryRun);

        // dry-run はリフレッシュしないので Jellyfin 接続を作らない。
        ILibraryRefresher refresher = new NullLibraryRefresher();
        IDisposable? refresherDisposable = null;
        if (!dryRun)
            (refresher, refresherDisposable) = BuildRefresher(config);

        // 発行するか・どの publisher を使うかを純粋関数で決める(テスト可能)。取込失敗の de-dup ストアも
        // 発行経路でだけ実体を使う(発行しない走査(dry-run など)が失敗を記録して初回通知を潰さないため。ADR-0011)。
        // 成功要約と失敗は同じゲートで揃える。新規取込のトーストを止める設定は、ここではなく提示側
        // (トレイの publisher)で見る。失敗のトーストと失敗のストアはその設定に関係なく動かす(#152・ADR-0019)。
        var (summaryPub, failurePub, useFailureStore) = SelectPublishers(
            notify, dryRun, summaryPublisher, failurePublisher);
        IIngestFailureStore failureStore = useFailureStore ? failureStoreFacet : new NullIngestFailureStore();
        // 保留のストアも同じゲート(ADR-0021)。発行しない走査が保留を記録して、初めて保留したときのトーストを潰さないため。
        IHeldRecordingStore heldStore = useFailureStore ? heldStoreFacet : new NullHeldRecordingStore();
        IIngestHeldPublisher heldPub = useFailureStore && heldPublisher is not null ? heldPublisher : new NullIngestHeldPublisher();

        // ジャンルの優先順位: programs.json の Defaults.Genre(明示時)> radirec.json の Genre > 既定。
        var genre = string.IsNullOrWhiteSpace(programs.Defaults.Genre)
            ? config.Genre
            : programs.Defaults.Genre;
        if (string.IsNullOrWhiteSpace(genre))
            genre = TagMapper.DefaultGenre;

        var tagReader = new TagLibTagReader();

        // らじれこ(M4A)と radikool(MP3)を CanHandle で振り分ける。radikool は識別に
        // タグ(局=genre)を要するため tagReader を渡す。
        var rajireko = new RajirekoImporter(config.KnownRecordingKinds);
        var importer = new SourceRouter(
        [
            rajireko,
            new RadikoolImporter(tagReader),
        ]);

        var service = new IngestService(
            new FileSystemRecordingSource(fileSystem, config.RadioRoot),
            importer,
            new StabilityChecker(TimeProvider.System, TimeSpan.FromSeconds(config.StabilitySeconds)),
            new RecordingSkipPolicy(config.SkipPatterns),
            tagReader,
            assembler,
            new LocalFolderTarget(fileSystem, new TagLibTagWriter(), new TagLibAudioContentComparer(), config.OutputRoot),
            ledger,
            TimeProvider.System,
            genre,
            refresher,
            summaryPub,
            failureStore,
            failurePub,
            heldStore,
            heldPub);

        return (service, Combine(ledgerDisposable, refresherDisposable));
    }

    /// <summary>
    /// シグナルの publisher と、失敗 de-dup ストアを使うかを決める純粋関数(テスト可能)。
    /// トレイ常駐移行(ADR-0012)後、シグナルが取込通知として提示される唯一の経路は呼び出し側が注入する
    /// in-process publisher(トレイのトースト)。両 publisher が注入され、dry-run でないときだけ発行し、
    /// それ以外(未注入・notify=false・dry-run)は発行しない。
    /// 新規取込のトーストを止める設定(旧 <see cref="RadirecConfig.NotificationsEnabled"/>)はここでは見ない。止めても
    /// 失敗のトーストは出て、失敗のストアにも記録されるように、止めるのは提示側で成功要約だけにする(#152・ADR-0019)。
    /// 片側だけの注入は、失敗を「記録するのに通知しない」ADR-0011 のサイレント抑制を招くため禁止する。
    /// </summary>
    public static (IIngestSummaryPublisher Summary, IIngestFailurePublisher Failure, bool UseFailureStore) SelectPublishers(
        bool notify, bool dryRun,
        IIngestSummaryPublisher? injectedSummary, IIngestFailurePublisher? injectedFailure)
    {
        if (injectedSummary is null != (injectedFailure is null))
            throw new ArgumentException(
                "summaryPublisher と failurePublisher は両方指定するか両方省略してください(片側のみの注入は不可)。");

        if (injectedSummary is null || !notify || dryRun)
            return (new NullIngestSummaryPublisher(), new NullIngestFailurePublisher(), false);

        return (injectedSummary, injectedFailure!, true);
    }

    /// <summary>
    /// config から統合設定の再適用(ADR-0016)を組み立てる。取り込みと同じ放送局・番組の解決、出力先の規則
    /// (<see cref="LocalFolderTarget"/>)、台帳を使う。dry-run は取り込みと同じく台帳を読み取り専用で開き
    /// (無ければ空)、Jellyfin 接続を作らない。台帳のフォルダに届かなければ空とは扱わず
    /// <see cref="LedgerUnreachableException"/> を投げる(#216)。実行(dry-run でない)は台帳を書き込み用に開き、開けなければ
    /// 同じく <see cref="LedgerUnreachableException"/>(<see cref="LedgerFile.OpenWritable"/>。#228)。
    /// </summary>
    /// <returns>再適用と、破棄すべきリソース(台帳・Jellyfin 接続)。</returns>
    public static (ReapplyService Service, IDisposable? Disposable) BuildReapply(RadirecConfig config, bool dryRun)
        => BuildReapply(config, dryRun, LoadReapplySettings(config));

    /// <summary>
    /// 再適用が使う番組・放送局の設定(<c>programs.json</c> / <c>stations.json</c>)をディスクから読む。
    /// トレイ(#126)は確認(dry-run)のときに 1 度だけ読み、実行にも同じものを渡す(確認の後にファイルが
    /// 編集されても、見せていない設定で動かさないため)。
    /// </summary>
    public static ReapplySettings LoadReapplySettings(RadirecConfig config) => new(
        ConfigLoader.LoadOptional<StationMasterConfig>(config.StationsFile),
        ConfigLoader.LoadOptional<ProgramMasterConfig>(config.ProgramsFile));

    /// <summary>
    /// 読み込み済みの番組・放送局の設定で再適用を組み立てる(<paramref name="settings"/> 以外にディスクの
    /// <c>programs.json</c> / <c>stations.json</c> は読まない)。
    /// </summary>
    public static (ReapplyService Service, IDisposable? Disposable) BuildReapply(
        RadirecConfig config, bool dryRun, ReapplySettings settings)
    {
        var fileSystem = new FileSystem();
        var (stations, programs) = settings;

        var (ledger, _, _, ledgerDisposable) = BuildLedger(config.LedgerPath, dryRun);

        ILibraryRefresher refresher = new NullLibraryRefresher();
        IDisposable? refresherDisposable = null;
        if (!dryRun)
            (refresher, refresherDisposable) = BuildRefresher(config);

        var comparer = new TagLibAudioContentComparer();
        var service = new ReapplyService(
            fileSystem,
            ledger,
            new RajirekoImporter(config.KnownRecordingKinds),
            new StationResolver(stations),
            new ProgramResolver(programs),
            new LocalFolderTarget(fileSystem, new TagLibTagWriter(), comparer, config.OutputRoot),
            new TagLibTagReader(),
            comparer,
            refresher,
            programs.KeepSeparate);

        return (service, Combine(ledgerDisposable, refresherDisposable));
    }

    /// <summary>
    /// config から Jellyfin リフレッシャを組み立てる(<c>refresh</c> コマンドでも再利用)。
    /// URL / API キーが未設定なら <see cref="NullLibraryRefresher"/>(何もしない)を返す。
    /// URL の形式が不正なら <see cref="MisconfiguredLibraryRefresher"/>(呼ぶと失敗する)を返す。
    /// </summary>
    public static (ILibraryRefresher Refresher, IDisposable? Disposable) BuildRefresher(RadirecConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.JellyfinUrl) || string.IsNullOrWhiteSpace(config.JellyfinApiKey))
            return (new NullLibraryRefresher(), null);

        // BaseAddress は末尾スラッシュ必須("Library/Refresh" 相対解決のため)。
        // URL が不正形式でも取り込み(主機能)をブロックしない。ただし未設定とは区別し、更新のたびに
        // 失敗として報告して設定の誤りに気づけるようにする(#133)。
        if (!Uri.TryCreate(config.JellyfinUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
            return (new MisconfiguredLibraryRefresher(
                $"JellyfinUrl の形式が不正です({config.JellyfinUrl})。radirec.json を確認してください。"), null);

        // 既定 100 秒だと更新遅延時に取込コマンドが長く固まるため、明示的に短くする。
        var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        return (new JellyfinLibraryRefresher(http, config.JellyfinApiKey), http);
    }

    private static IDisposable? Combine(IDisposable? a, IDisposable? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new CompositeDisposable(a, b);
    }

    private sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
                item.Dispose();
        }
    }

    private static (ILedger Ledger, IIngestFailureStore FailureStore, IHeldRecordingStore HeldStore, IDisposable? Disposable)
        BuildLedger(string ledgerPath, bool dryRun)
    {
        if (!dryRun)
        {
            // 開けなければ LedgerUnreachableException(#228)。設定の反映の失敗を台帳のせいと見分けられるようにする。
            var writable = LedgerFile.OpenWritable(ledgerPath);
            // 同一インスタンスが台帳 + 失敗 de-dup ストア + 保留のストア(ADR-0021)を兼ねる。
            return (writable, writable, writable, writable);
        }

        // dry-run: 副作用を避ける。既存台帳は読み取り専用、無ければ NullLedger。失敗 de-dup は notify 経路
        // でしか使わない(dry-run は発行しない)ため、ここでは Null ストアを返す。台帳のフォルダに届かなければ、
        // 「台帳が無い」として全件を予定と答えず失敗にする(IOException。CLI は 5。#216)。
        if (LedgerFile.Exists(ledgerPath))
        {
            var readOnly = new SqliteLedger(ledgerPath, readOnly: true);
            return (readOnly, new NullIngestFailureStore(), new NullHeldRecordingStore(), readOnly);
        }

        return (new NullLedger(), new NullIngestFailureStore(), new NullHeldRecordingStore(), null);
    }
}
