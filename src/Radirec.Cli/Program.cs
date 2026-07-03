using Radirec.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("radirec");
    config.AddCommand<IngestCommand>("ingest")
        .WithDescription("らじれこ録音を正規化して取り込む(--dry-run で予定表示)")
        .WithExample("ingest", "--dry-run")
        .WithExample("ingest", "--config", "radirec.json");
    config.AddCommand<RefreshCommand>("refresh")
        .WithDescription("Jellyfin のライブラリ更新を手動で起動する")
        .WithExample("refresh", "--config", "radirec.json");
});

return await app.RunAsync(args);
