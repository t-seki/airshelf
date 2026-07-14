using Radirec.Hosting.Control;

namespace Radirec.Tests;

/// <summary>
/// 制御チャネル(ADR-0010)の round-trip と dispatch・フォールバックを in-process で検証する。
/// PipeSecurity(認可 ACL)は Windows 専用のため隔離済みで、ここでは Byte モードの往復・コマンド
/// dispatch・未応答フォールバックを WSL/Linux CI でも回せる部分として検証する。ACL は Windows 実機検証。
/// </summary>
public sealed class ControlChannelTests
{
    // 実サービスや並行テストの固定名パイプと衝突しないよう、テストごとにユニーク名を使う。
    private static string UniquePipeName() => $"radirec-control-test-{Guid.NewGuid():N}";

    private sealed class FakeControl : IServiceControl
    {
        public int RescanCount;
        public ServiceStatusSnapshot Snapshot { get; init; } = new()
        {
            Version = "9.9.9-test",
            StartedUtc = DateTimeOffset.UnixEpoch,
            RadioRoot = @"I:\radio\tmp",
            Watching = true,
        };

        public void RequestRescan() => Interlocked.Increment(ref RescanCount);
        public ServiceStatusSnapshot GetStatus() => Snapshot;
    }

    private static async Task<ControlResponse> RoundTripAsync(
        IServiceControl control, string command, string pipeName)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new ControlPipeServer(control, pipeName);
        var serverTask = server.RunAsync(cts.Token);

        var response = await ControlClient.SendAsync(
            command, pipeName, connectTimeout: TimeSpan.FromSeconds(5), cancellationToken: cts.Token);

        cts.Cancel();
        await serverTask; // accept ループが停止要求で綺麗に抜けること。
        return response;
    }

    [Fact]
    public async Task rescan_は要求チャネルへdispatchされ受理を返す()
    {
        var control = new FakeControl();
        var response = await RoundTripAsync(control, ControlContract.Commands.Rescan, UniquePipeName());

        Assert.True(response.Ok);
        Assert.Null(response.Error);
        Assert.Equal(1, control.RescanCount);
    }

    [Fact]
    public async Task status_はスナップショットを往復して返す()
    {
        var control = new FakeControl();
        var response = await RoundTripAsync(control, ControlContract.Commands.Status, UniquePipeName());

        Assert.True(response.Ok);
        var status = response.ResultAs<ServiceStatusSnapshot>();
        Assert.NotNull(status);
        Assert.Equal(control.Snapshot.Version, status!.Version);
        Assert.Equal(control.Snapshot.RadioRoot, status.RadioRoot);
        Assert.Equal(control.Snapshot.Watching, status.Watching);
        Assert.Equal(control.Snapshot.StartedUtc, status.StartedUtc);
    }

    [Fact]
    public async Task 未知コマンドはエラー応答_dispatchはされない()
    {
        var control = new FakeControl();
        var response = await RoundTripAsync(control, "bogus-command", UniquePipeName());

        Assert.False(response.Ok);
        Assert.NotNull(response.Error);
        Assert.Equal(0, control.RescanCount);
    }

    [Fact]
    public async Task サービス未起動なら接続タイムアウトでControlUnavailable()
    {
        // サーバを立てないパイプ名に短い connect timeout で繋ぐ。
        await Assert.ThrowsAsync<ControlUnavailableException>(() =>
            ControlClient.SendAsync(
                ControlContract.Commands.Status,
                UniquePipeName(),
                connectTimeout: TimeSpan.FromMilliseconds(300)));
    }
}
