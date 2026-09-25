using Radirec.Cli;
using Radirec.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("radirec");
    config.SetApplicationVersion(VersionInfo.Current);
    // 知らないオプションはエラーにする(既定では黙って読み捨てる)。削った --config / --root を付けた古いスクリプトが、
    // 気づかれないまま既定の設定・台帳で動いてしまわないように(ADR-0020)。
    config.UseStrictParsing();
    config.AddCommand<IngestCommand>("ingest")
        .WithDescription("らじれこ録音を正規化して取り込む(--dry-run で予定表示)")
        .WithExample("ingest", "--dry-run");
    config.AddCommand<ReapplyCommand>("reapply")
        .WithDescription("今の番組の設定(統合・正規名)を取り込み済みの回へ再適用する(--dry-run で予定表示)")
        .WithExample("reapply", "--dry-run");
    config.AddCommand<RefreshCommand>("refresh")
        .WithDescription("Jellyfin のライブラリ更新を手動で起動する")
        .WithExample("refresh");
    // 常駐(service)と制御 verb(rescan/status/reload)は撤去した。常駐はトレイ(Radirec.Gui)が
    // 担い、制御はトレイメニューから in-process で行う(ADR-0012)。radirec.exe は dev/batch 用の
    // 単発 CLI(ingest / reapply / refresh)に縮小。一回きりの移行のための ingest --root と organize-source、
    // 設定ファイルを指す --config は削った(ADR-0020)。設定は常にトレイと同じ既定の radirec.json を使う。
});

return await app.RunAsync(args);
