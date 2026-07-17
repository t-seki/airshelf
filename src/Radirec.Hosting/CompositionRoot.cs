using System.IO.Abstractions;
using Radirec.Core.Configuration;
using Radirec.Core.Ingestion;
using Radirec.Core.Jellyfin;
using Radirec.Core.Ledger;
using Radirec.Core.Normalization;
using Radirec.Core.Output;
using Radirec.Core.Sources;

namespace Radirec.Hosting;

/// <summary>設定から取り込みサービス一式を組み立てる合成ルート(M1 は手配線)。</summary>
public static class CompositionRoot
{
    /// <param name="dryRun">
    /// true なら台帳を副作用なく扱う(既存 DB は読み取り専用で開き、無ければ NullLedger)。
    /// </param>
    /// <param name="rootOverride">
    /// 監視ルートの上書き(radikool 一括移行の一度きり実行で <c>--root</c> から渡す)。
    /// null なら <see cref="RadirecConfig.RadioRoot"/> を使う。
    /// </param>
    /// <param name="notify">
    /// 取込要約シグナルを発行するか。**常駐サービス経路のみ true**(ADR-0008)。手動 <c>ingest</c> は
    /// 対話コマンドで受け手 GUI へ通知する意味が無く、未登録ソースへの書込失敗も招くため false。
    /// </param>
    /// <returns>サービスと、破棄すべきリソース(台帳。NullLedger 時は null)。</returns>
    public static (IngestService Service, IDisposable? Disposable) Build(
        RadirecConfig config, bool dryRun, string? rootOverride = null, bool notify = false)
    {
        var fileSystem = new FileSystem();

        var stations = ConfigLoader.LoadOptional<StationMasterConfig>(config.StationsFile);
        var programs = ConfigLoader.LoadOptional<ProgramMasterConfig>(config.ProgramsFile);

        var assembler = new EpisodeAssembler(
            new StationResolver(stations),
            new ProgramResolver(programs));

        var (ledger, failureStoreFacet, ledgerDisposable) = BuildLedger(config.LedgerPath, dryRun);

        // dry-run はリフレッシュしないので Jellyfin 接続を作らない。
        ILibraryRefresher refresher = new NullLibraryRefresher();
        IDisposable? refresherDisposable = null;
        if (!dryRun)
            (refresher, refresherDisposable) = BuildRefresher(config);

        // 発行はサービス経路のみ(notify)。dry-run / 通知無効 / 非 Windows は発行しない。取込失敗の
        // de-dup ストアも notify 経路でだけ実体を使う(非 notify 走査が失敗を記録して初回通知を潰さないため。
        // ADR-0011)。成功要約(1001)と失敗(1002)は同じゲートで揃える。
        IIngestSummaryPublisher summaryPublisher = new NullIngestSummaryPublisher();
        IIngestFailurePublisher failurePublisher = new NullIngestFailurePublisher();
        IIngestFailureStore failureStore = new NullIngestFailureStore();
        if (notify && !dryRun && config.NotificationsEnabled && OperatingSystem.IsWindows())
        {
            summaryPublisher = new EventLogIngestSummaryPublisher();
            failurePublisher = new EventLogIngestFailurePublisher();
            failureStore = failureStoreFacet;
        }

        // ジャンルの優先順位: programs.json の Defaults.Genre(明示時)> radirec.json の Genre > 既定。
        var genre = string.IsNullOrWhiteSpace(programs.Defaults.Genre)
            ? config.Genre
            : programs.Defaults.Genre;
        if (string.IsNullOrWhiteSpace(genre))
            genre = TagMapper.DefaultGenre;

        var tagReader = new TagLibTagReader();

        // らじれこ(M4A)と radikool(MP3)を CanHandle で振り分ける。radikool は識別に
        // タグ(局=genre)を要するため tagReader を渡す。
        var importer = new SourceRouter(
        [
            new RajirekoImporter(config.KnownRecordingKinds),
            new RadikoolImporter(tagReader),
        ]);

        var service = new IngestService(
            new FileSystemRecordingSource(fileSystem, rootOverride ?? config.RadioRoot),
            importer,
            new StabilityChecker(TimeProvider.System, TimeSpan.FromSeconds(config.StabilitySeconds)),
            new RecordingSkipPolicy(config.SkipPatterns),
            tagReader,
            assembler,
            new LocalFolderTarget(fileSystem, new TagLibTagWriter(), config.OutputRoot),
            ledger,
            TimeProvider.System,
            genre,
            refresher,
            summaryPublisher,
            failureStore,
            failurePublisher);

        return (service, Combine(ledgerDisposable, refresherDisposable));
    }

    /// <summary>
    /// config から Jellyfin リフレッシャを組み立てる(<c>refresh</c> コマンドでも再利用)。
    /// URL / API キーが未設定なら <see cref="NullLibraryRefresher"/>(何もしない)を返す。
    /// </summary>
    public static (ILibraryRefresher Refresher, IDisposable? Disposable) BuildRefresher(RadirecConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.JellyfinUrl) || string.IsNullOrWhiteSpace(config.JellyfinApiKey))
            return (new NullLibraryRefresher(), null);

        // BaseAddress は末尾スラッシュ必須("Library/Refresh" 相対解決のため)。
        // URL が不正形式でも取り込み(主機能)をブロックしない。リフレッシュだけ無効化する。
        if (!Uri.TryCreate(config.JellyfinUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
            return (new NullLibraryRefresher(), null);

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

    private static (ILedger Ledger, IIngestFailureStore FailureStore, IDisposable? Disposable) BuildLedger(
        string ledgerPath, bool dryRun)
    {
        if (!dryRun)
        {
            var writable = new SqliteLedger(ledgerPath);
            return (writable, writable, writable); // 同一インスタンスが台帳 + 失敗 de-dup ストアを兼ねる。
        }

        // dry-run: 副作用を避ける。既存台帳は読み取り専用、無ければ NullLedger。失敗 de-dup は notify 経路
        // でしか使わない(dry-run は発行しない)ため、ここでは Null ストアを返す。
        if (File.Exists(ledgerPath))
        {
            var readOnly = new SqliteLedger(ledgerPath, readOnly: true);
            return (readOnly, new NullIngestFailureStore(), readOnly);
        }

        return (new NullLedger(), new NullIngestFailureStore(), null);
    }
}
