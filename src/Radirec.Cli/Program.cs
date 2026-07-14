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
    config.AddCommand<RescanCommand>("rescan")
        .WithDescription("常駐サービスへ手動再スキャンを要求する(制御チャネル)");
    config.AddCommand<StatusCommand>("status")
        .WithDescription("常駐サービスのライブ状態を取得して表示する(制御チャネル)");
    config.AddCommand<ReloadCommand>("reload")
        .WithDescription("常駐サービスに radirec.json の再読込を要求する(制御チャネル)");
});

return await app.RunAsync(args);
