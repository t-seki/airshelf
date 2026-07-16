using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HostControl = Radirec.Hosting.Control;   // サービス側(実 ControlPipeServer / IServiceControl)
using Radirec.Hosting.Control;                 // FakeControl が実装する IServiceControl 等
using GuiControl = Radirec.Gui.Core.Control;   // 利用者 GUI 側(Gui.Core の自前クライアント + DTO)

namespace Radirec.Tests;

/// <summary>
/// 軸B(制御チャネルの GUI 化)のドリフト検出。<c>Radirec.Gui.Core.Control</c> の**自前 DTO / 自前
/// クライアント**が、発行側 <c>Radirec.Hosting.Control</c> の凍結 wire 契約に追従していることを検証する。
/// (1) 送出側: Gui の要求 DTO が凍結 JSON へバイト一致。(2) 受け手側: Gui の状態 DTO が凍結応答を解析。
/// (3) 往復: 実 <see cref="ControlPipeServer"/> と Gui クライアントで Byte モードパイプの往復が成立
/// (WSL/Linux CI でも回る。ACL は Windows 実機検証)。型は共有せず JSON 契約だけで繋ぐ(ADR-0010)。
/// </summary>
public sealed class GuiControlChannelTests
{
    private static string UniquePipeName() => $"radirec-control-guitest-{Guid.NewGuid():N}";

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

    // ---- (1) 送出側: 自前 DTO が凍結 JSON へバイト一致 ----

    [Fact]
    public void Gui_要求DTOは凍結JSONへバイト一致する_送出側()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new GuiControl.ControlRequest { Command = GuiControl.ControlContract.Commands.Rescan },
            GuiControl.ControlContract.Json);

        Assert.Equal(
            """{"schemaVersion":1,"command":"rescan"}""",
            Encoding.UTF8.GetString(bytes));
    }

    // ---- (2) 受け手側: 自前状態 DTO が凍結応答 JSON を解析 ----

    [Fact]
    public void Gui_状態DTOは凍結応答JSONを解析できる_受け手側()
    {
        const string frozen = """
            {"schemaVersion":1,"ok":true,"result":{"version":"1.2.3","startedUtc":"2026-07-14T01:02:03+00:00","radioRoot":"I:\\radio","watching":true,"lastScanCompletedUtc":"2026-07-14T04:05:06+00:00","lastScanTrigger":"手動","lastImported":3,"lastFailed":1}}
            """;

        var response = JsonSerializer.Deserialize<GuiControl.ControlResponse>(frozen, GuiControl.ControlContract.Json);

        Assert.NotNull(response);
        Assert.True(response!.Ok);
        var status = response.ResultAs<GuiControl.ServiceStatusSnapshot>();
        Assert.NotNull(status);
        Assert.Equal("1.2.3", status!.Version);
        Assert.Equal(@"I:\radio", status.RadioRoot);
        Assert.True(status.Watching);
        Assert.Equal("手動", status.LastScanTrigger);
        Assert.Equal(3, status.LastImported);
        Assert.Equal(1, status.LastFailed);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 4, 5, 6, TimeSpan.Zero), status.LastScanCompletedUtc);
    }

    [Fact]
    public void Gui_省略フィールドは既定へ戻る_受け手側()
    {
        // 未走査(lastScanCompletedUtc / lastScanTrigger を省く)応答を解析できること。
        const string frozen = """
            {"schemaVersion":1,"ok":true,"result":{"version":"9.9.9","startedUtc":"2026-07-14T00:00:00+00:00","radioRoot":"Z:\\radio","watching":false,"lastImported":0,"lastFailed":0}}
            """;

        var response = JsonSerializer.Deserialize<GuiControl.ControlResponse>(frozen, GuiControl.ControlContract.Json);
        var status = response!.ResultAs<GuiControl.ServiceStatusSnapshot>();

        Assert.NotNull(status);
        Assert.False(status!.Watching);
        Assert.Null(status.LastScanCompletedUtc);
        Assert.Null(status.LastScanTrigger);
    }

    // ---- (3) 往復: 実サーバ ↔ Gui クライアント ----

    private static async Task WithServerAsync(IServiceControl control, string pipeName, Func<Task> body)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new ControlPipeServer(control, pipeName);
        var serverTask = server.RunAsync(cts.Token);
        try
        {
            await body();
        }
        finally
        {
            cts.Cancel();
            await serverTask; // accept ループが停止要求で綺麗に抜けること。
        }
    }

    [Fact]
    public async Task Gui_クライアントで実サーバへrescan往復()
    {
        var control = new FakeControl();
        var pipe = UniquePipeName();
        await WithServerAsync(control, pipe, async () =>
            await GuiControl.ControlClient.RequestRescanAsync(pipe));
        Assert.Equal(1, control.RescanCount);
    }

    [Fact]
    public async Task Gui_クライアントで実サーバからstatus取得()
    {
        var control = new FakeControl();
        GuiControl.ServiceStatusSnapshot? got = null;
        var pipe = UniquePipeName();
        await WithServerAsync(control, pipe, async () =>
            got = await GuiControl.ControlClient.GetStatusAsync(pipe));

        Assert.NotNull(got);
        Assert.Equal("9.9.9-test", got!.Version);
        Assert.Equal(@"I:\radio\tmp", got.RadioRoot);
        Assert.True(got.Watching);
    }

    [Fact]
    public async Task Gui_クライアントで実サーバへreload往復_受理()
    {
        var control = new FakeControl { ReloadResult = ReloadResult.Ok() };
        GuiControl.ReloadOutcome? outcome = null;
        var pipe = UniquePipeName();
        await WithServerAsync(control, pipe, async () =>
            outcome = await GuiControl.ControlClient.RequestReloadAsync(pipe));

        Assert.NotNull(outcome);
        Assert.True(outcome!.Accepted);
        Assert.Equal(1, control.ReloadCount);
    }

    [Fact]
    public async Task Gui_reload拒否はOutcomeへ正規化される()
    {
        var control = new FakeControl { ReloadResult = ReloadResult.Failed("config 検証失敗") };
        GuiControl.ReloadOutcome? outcome = null;
        var pipe = UniquePipeName();
        await WithServerAsync(control, pipe, async () =>
            outcome = await GuiControl.ControlClient.RequestReloadAsync(pipe));

        Assert.NotNull(outcome);
        Assert.False(outcome!.Accepted);
        Assert.Equal("config 検証失敗", outcome.Error);
    }

    [Fact]
    public async Task Gui_サービス未起動はControlUnavailableへ縮退()
    {
        // 誰も listen していないユニーク名 → connect timeout → 縮退例外。
        await Assert.ThrowsAsync<GuiControl.ControlUnavailableException>(() =>
            GuiControl.ControlClient.SendAsync(
                GuiControl.ControlContract.Commands.Status,
                UniquePipeName(),
                connectTimeout: TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Gui_不正な応答フレームはControlUnavailableへ縮退する()
    {
        // ControlResponse でない body を返す相手でも、例外を正規化してトレイを落とさないこと
        // (async void ハンドラは ControlUnavailableException しか握らない)。
        var pipe = UniquePipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RespondWithRawFrameAsync(pipe, Encoding.UTF8.GetBytes("not-json"), cts.Token);

        await Assert.ThrowsAsync<GuiControl.ControlUnavailableException>(() =>
            GuiControl.ControlClient.GetStatusAsync(pipe, cts.Token));
        await serverTask;
    }

    // ---- (4) 契約定数のドリフト検出(既定 PipeName を含む。往復テストはユニーク名を使うため未カバー) ----

    [Fact]
    public void Gui_契約定数は発行側と一致する()
    {
        Assert.Equal(HostControl.ControlContract.PipeName, GuiControl.ControlContract.PipeName);
        Assert.Equal(HostControl.ControlContract.SchemaVersion, GuiControl.ControlContract.SchemaVersion);
        Assert.Equal(HostControl.ControlContract.MaxMessageBytes, GuiControl.ControlContract.MaxMessageBytes);
        Assert.Equal(HostControl.ControlContract.Commands.Rescan, GuiControl.ControlContract.Commands.Rescan);
        Assert.Equal(HostControl.ControlContract.Commands.Status, GuiControl.ControlContract.Commands.Status);
        Assert.Equal(HostControl.ControlContract.Commands.Reload, GuiControl.ControlContract.Commands.Reload);
    }

    /// <summary>要求フレームを読み捨て、任意の生バイトを 1 フレーム(4バイト長 + body)で返す最小サーバ。</summary>
    private static async Task RespondWithRawFrameAsync(string pipeName, byte[] body, CancellationToken ct)
    {
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(ct);

        var header = new byte[4];
        await ReadExactAsync(server, header, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        await ReadExactAsync(server, new byte[length], ct);

        var outHeader = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(outHeader, body.Length);
        await server.WriteAsync(outHeader, ct);
        await server.WriteAsync(body, ct);
        await server.FlushAsync(ct);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }
}
