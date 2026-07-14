using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Radirec.Hosting.Control;

namespace Radirec.Cli;

/// <summary>
/// 制御チャネル(ADR-0010)の named pipe サーバをホストする常駐サービス。`service` verb のホストにのみ
/// 載る(対話 ingest/refresh には載せない)。実体は <see cref="ControlPipeServer"/> で、worker と同一
/// インスタンスの <see cref="IServiceControl"/> を共有してコマンドを dispatch する。
/// </summary>
public sealed class ControlListenerService(
    IServiceControl control,
    ILogger<ControlListenerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("制御チャネル listen 開始(pipe: {Pipe})", ControlContract.PipeName);
        var server = new ControlPipeServer(
            control,
            onConnectionError: ex => log.LogWarning(ex, "制御チャネル接続の処理でエラー(listen 継続)"));
        await server.RunAsync(stoppingToken);
    }
}
