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
    config.AddCommand<ServiceCommand>("service")
        .WithDescription("Windows サービスとして常駐し録音を自動取り込みする");
});

return await app.RunAsync(args);
