using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Airshelf.Hosting.Control;

namespace Airshelf.Tests;

/// <summary>
/// トレイ(Airshelf.Gui の <c>Program.cs</c>)がワーカーをホストするときの前提を、同じ版の Microsoft.Extensions.Hosting で固定する(#217)。
/// .NET 10 から <see cref="BackgroundService"/> は <c>ExecuteAsync</c> の全体を別のタスクで走らせる
/// (https://learn.microsoft.com/dotnet/core/compatibility/extensions/10.0/backgroundservice-executeasync-task)。
/// そのため、起動処理(<see cref="SerialLoop"/> の <c>start</c>)の例外は <c>host.StartAsync()</c> に伝わらない。
/// </summary>
public sealed class BackgroundServiceStartTests
{
    // RadioMonitorWorker.ExecuteAsync と同じ形:最初の await の前(同期の部分)で起動処理が投げる。
    private sealed class ThrowingStartWorker : BackgroundService
    {
        public SerialLoop Loop { get; } = new();

        public List<Exception> StartFailures { get; } = [];

        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            Loop.RunAsync(
                start: () => throw new UnauthorizedAccessException("Access to the path 'OUTPUT\\locked' is denied."),
                loop: _ => Task.CompletedTask,
                onStopped: () => { },
                onUnexpectedExit: _ => { },
                onStartFailed: StartFailures.Add,
                stoppingToken);
    }

    [Fact]
    public async Task ExecuteAsyncの同期の部分で投げても_host_StartAsyncは成功し_起動処理の失敗はワーカーの側でしか捕まえられない()
    {
        var worker = new ThrowingStartWorker();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostedService>(worker);
        using var host = builder.Build();

        // .NET 9 までは、ここで UnauthorizedAccessException が投げられていた(#207 の ContinueWith の前提)。
        await host.StartAsync(TestContext.Current.CancellationToken);

        // 例外はホストの起動ではなく、ワーカーの実行のタスクに残る。
        var executeTask = worker.ExecuteTask!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executeTask);

        Assert.IsType<UnauthorizedAccessException>(Assert.Single(worker.StartFailures));
        Assert.False(worker.Loop.Running);
        Assert.IsType<UnauthorizedAccessException>(worker.Loop.StartFailure);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
