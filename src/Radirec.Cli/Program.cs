using Radirec.Cli;
using Radirec.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("radirec");
    config.SetApplicationVersion(VersionInfo.Current);
    config.AddCommand<IngestCommand>("ingest")
        .WithDescription("らじれこ録音を正規化して取り込む(--dry-run で予定表示)")
        .WithExample("ingest", "--dry-run")
        .WithExample("ingest", "--config", "radirec.json");
    config.AddCommand<RefreshCommand>("refresh")
        .WithDescription("Jellyfin のライブラリ更新を手動で起動する")
        .WithExample("refresh", "--config", "radirec.json");
    // 常駐(service)と制御 verb(rescan/status/reload)は撤去した。常駐はトレイ(Radirec.Gui)が
    // 担い、制御はトレイメニューから in-process で行う(ADR-0012)。radirec.exe は dev/batch 用の
    // 単発 CLI(ingest / refresh)に縮小。
});

return await app.RunAsync(args);
