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
        public int ReloadCount;
        public ReloadResult ReloadResult { get; init; } = ReloadResult.Ok();
        public ServiceStatusSnapshot Snapshot { get; init; } = new()
        {
            Version = "9.9.9-test",
            StartedUtc = DateTimeOffset.UnixEpoch,
            RadioRoot = @"I:\radio\tmp",
            Watching = true,
        };

        public void RequestRescan() => Interlocked.Increment(ref RescanCount);
        public ServiceStatusSnapshot GetStatus() => Snapshot;

        public ReloadResult RequestReload()
        {
            Interlocked.Increment(ref ReloadCount);
            return ReloadResult;
        }
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
    public async Task reload_受理はdispatchされ受理を返す()
    {
        var control = new FakeControl { ReloadResult = ReloadResult.Ok() };
        var response = await RoundTripAsync(control, ControlContract.Commands.Reload, UniquePipeName());

        Assert.True(response.Ok);
        Assert.Equal(1, control.ReloadCount);
    }

    [Fact]
    public async Task reload_検証失敗はエラー応答として往復する()
    {
        var control = new FakeControl { ReloadResult = ReloadResult.Failed("設定の読み込みに失敗しました: bad json") };
        var response = await RoundTripAsync(control, ControlContract.Commands.Reload, UniquePipeName());

        Assert.False(response.Ok);
        Assert.Contains("bad json", response.Error);
        Assert.Equal(1, control.ReloadCount);
    }

    [Fact]
    public async Task ハンドラ例外は失敗応答に正規化される_接続を落とさない()
    {
        // 回帰: コマンドハンドラ(IServiceControl 実装)が throw しても応答を返さず接続を落とすと、
        // クライアントが誤って「サービス応答なし」になる。失敗応答へ正規化されること。
        var control = new ThrowingControl();
        var response = await RoundTripAsync(control, ControlContract.Commands.Reload, UniquePipeName());

        Assert.False(response.Ok);
        Assert.Contains("boom", response.Error);
    }

    private sealed class ThrowingControl : IServiceControl
    {
        public void RequestRescan() => throw new InvalidOperationException("boom");
        public ServiceStatusSnapshot GetStatus() => throw new InvalidOperationException("boom");
        public ReloadResult RequestReload() => throw new InvalidOperationException("boom");
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
    public async Task 無言クライアントのreadタイムアウトでlistenは停止しない()
    {
        // 回帰: 接続だけして送らないクライアントの read タイムアウト(OCE)を「停止要求」と誤認して
        // listener を永久終了させないこと。タイムアウト後の status が繋がれば listener は生きている。
        var pipe = UniquePipeName();
        var control = new FakeControl();
        var errors = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new ControlPipeServer(
            control, pipe,
            onConnectionError: _ => Interlocked.Increment(ref errors),
            readTimeout: TimeSpan.FromMilliseconds(200));
        var serverTask = server.RunAsync(cts.Token);

        // 接続のみ・無送信で read タイムアウトを誘発。単一インスタンス競合を避けるため、サーバが
        // タイムアウトを処理して accept ループへ復帰するまで待ってから status を繋ぐ(逐次化)。
        var silent = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await silent.ConnectAsync(5000, cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(600), cts.Token); // read タイムアウト(200ms)経過を待つ
        await silent.DisposeAsync();

        // タイムアウト後に status が繋がり成功すること = accept ループが生きている。
        var response = await ControlClient.SendAsync(
            ControlContract.Commands.Status, pipe,
            connectTimeout: TimeSpan.FromSeconds(5), cancellationToken: cts.Token);
        Assert.True(response.Ok);
        Assert.True(errors >= 1); // read タイムアウトは「接続エラー(listen 継続)」として扱われた

        cts.Cancel();
        await serverTask;
    }

    private sealed class BlockingControl : IServiceControl
    {
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);

        public void RequestRescan() { }
        public ServiceStatusSnapshot GetStatus() => new()
        {
            Version = "t", StartedUtc = DateTimeOffset.UnixEpoch, RadioRoot = "r", Watching = true,
        };

        public ReloadResult RequestReload()
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            return ReloadResult.Ok();
        }
    }

    [Fact]
    public async Task 遅いreloadは並行するstatusをブロックしない()
    {
        // 回帰(#43): 単一インスタンスだと遅い reload(同期 config 読込等)が status/rescan を凍結させる。
        // 並行 accept により、reload ハンドラがブロック中でも status が即応することを検証する。
        var pipe = UniquePipeName();
        var control = new BlockingControl();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new ControlPipeServer(control, pipe);
        var serverTask = server.RunAsync(cts.Token);

        // クライアント A: reload を送るとハンドラがブロックする(ここでは await しない)。
        var reloadTask = ControlClient.SendAsync(
            ControlContract.Commands.Reload, pipe,
            connectTimeout: TimeSpan.FromSeconds(5), readTimeout: TimeSpan.FromSeconds(15), cancellationToken: cts.Token);

        Assert.True(control.Entered.Wait(TimeSpan.FromSeconds(5)), "reload ハンドラに到達しなかった");

        // クライアント B: reload がブロック中でも status が即応する(= 並行に捌けている)。
        var status = await ControlClient.SendAsync(
            ControlContract.Commands.Status, pipe,
            connectTimeout: TimeSpan.FromSeconds(5), cancellationToken: cts.Token);
        Assert.True(status.Ok);

        control.Release.Set();
        var reload = await reloadTask;
        Assert.True(reload.Ok);

        cts.Cancel();
        await serverTask;
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
