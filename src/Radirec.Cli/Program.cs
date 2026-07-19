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
    config.AddCommand<OrganizeSourceCommand>("organize-source")
        .WithDescription("散在ソースを {放送局}/{番組}/ 構造へ仕分ける(--dry-run で予定表示)")
        .WithExample("organize-source", "--from", @"I:\radio\old", "--dry-run")
        .WithExample("organize-source", "--from", @"I:\radio\old", "--to", @"I:\radio\radirec\RADIO");
    // 常駐(service)と制御 verb(rescan/status/reload)は撤去した。常駐はトレイ(Radirec.Gui)が
    // 担い、制御はトレイメニューから in-process で行う(ADR-0012)。radirec.exe は dev/batch 用の
    // 単発 CLI(ingest / refresh)に縮小。
});

return await app.RunAsync(args);
