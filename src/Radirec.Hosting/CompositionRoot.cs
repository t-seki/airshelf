using System.IO.Abstractions;
using Radirec.Core.Configuration;
using Radirec.Core.Ingestion;
using Radirec.Core.Jellyfin;
using Radirec.Core.Ledger;
using Radirec.Core.Normalization;
using Radirec.Core.Organization;
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
    /// 取込通知を発行するか。**常駐経路のみ true**。手動 <c>ingest</c> は対話コマンドで、
    /// 結果は標準出力に出るためトーストを重ねる意味が無い(ADR-0012)。
    /// </param>
    /// <param name="summaryPublisher">
    /// 取込要約**シグナル**の publisher を注入する(現行の実装はトレイ常駐の in-process トースト。ADR-0012)。
    /// null なら発行しない(<see cref="SelectPublishers"/> が Null publisher を返す)。
    /// </param>
    /// <param name="failurePublisher">取込失敗**シグナル**の publisher。<paramref name="summaryPublisher"/> と同様。</param>
    /// <returns>サービスと、破棄すべきリソース(台帳。NullLedger 時は null)。</returns>
    public static (IngestService Service, IDisposable? Disposable) Build(
        RadirecConfig config, bool dryRun, string? rootOverride = null, bool notify = false,
        IIngestSummaryPublisher? summaryPublisher = null, IIngestFailurePublisher? failurePublisher = null)
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

        // 発行するか・どの publisher を使うかを純粋関数で決める(テスト可能)。取込失敗の de-dup ストアも
        // 発行経路でだけ実体を使う(非発行走査が失敗を記録して初回通知を潰さないため。ADR-0011)。
        // 成功要約と失敗は同じゲートで揃える。
        var (summaryPub, failurePub, useFailureStore) = SelectPublishers(
            notify, dryRun, config.NotificationsEnabled, summaryPublisher, failurePublisher);
        IIngestFailureStore failureStore = useFailureStore ? failureStoreFacet : new NullIngestFailureStore();

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
            new LocalFolderTarget(fileSystem, new TagLibTagWriter(), new TagLibAudioContentComparer(), config.OutputRoot),
            ledger,
            TimeProvider.System,
            genre,
            refresher,
            summaryPub,
            failureStore,
            failurePub);

        return (service, Combine(ledgerDisposable, refresherDisposable));
    }

    /// <summary>
    /// シグナルの publisher と、失敗 de-dup ストアを使うかを決める純粋関数(テスト可能)。
    /// トレイ常駐移行(ADR-0012)後、シグナルが取込通知として提示される唯一の経路は呼び出し側が注入する
    /// in-process publisher(トレイのトースト)。両 publisher が注入され、通知が有効で dry-run でないときだけ発行し、
    /// それ以外(未注入・notify=false・dry-run・通知無効)は発行しない。
    /// 片側だけの注入は、失敗を「記録するのに通知しない」ADR-0011 のサイレント抑制を招くため禁止する。
    /// </summary>
    public static (IIngestSummaryPublisher Summary, IIngestFailurePublisher Failure, bool UseFailureStore) SelectPublishers(
        bool notify, bool dryRun, bool notificationsEnabled,
        IIngestSummaryPublisher? injectedSummary, IIngestFailurePublisher? injectedFailure)
    {
        if (injectedSummary is null != (injectedFailure is null))
            throw new ArgumentException(
                "summaryPublisher と failurePublisher は両方指定するか両方省略してください(片側のみの注入は不可)。");

        if (injectedSummary is null || !notify || dryRun || !notificationsEnabled)
            return (new NullIngestSummaryPublisher(), new NullIngestFailurePublisher(), false);

        return (injectedSummary, injectedFailure!, true);
    }

    /// <summary>
    /// config からソース仕分け器(<c>organize-source</c> コマンド。#81)を組み立てる。取込 importer
    /// (らじれこ/radikool 振り分け)と放送局 resolver・スキップポリシーを ingest と同じ流儀で再利用する。
    /// 台帳・出力先・Jellyfin は使わない(ソースの物理再配置だけを行うため)。
    /// </summary>
    public static SourceOrganizer BuildOrganizer(RadirecConfig config, string from, string to)
    {
        var fileSystem = new FileSystem();
        var stations = ConfigLoader.LoadOptional<StationMasterConfig>(config.StationsFile);
        var tagReader = new TagLibTagReader();

        var importer = new SourceRouter(
        [
            new RajirekoImporter(config.KnownRecordingKinds),
            new RadikoolImporter(tagReader),
        ]);

        return new SourceOrganizer(
            fileSystem,
            new FileSystemRecordingSource(fileSystem, from),
            importer,
            new StationResolver(stations),
            new StabilityChecker(TimeProvider.System, TimeSpan.FromSeconds(config.StabilitySeconds)),
            new RecordingSkipPolicy(config.SkipPatterns),
            to);
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
