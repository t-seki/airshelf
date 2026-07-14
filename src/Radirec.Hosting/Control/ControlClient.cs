using System.IO.Pipes;
using System.Text.Json;

namespace Radirec.Hosting.Control;

/// <summary>サービスが未起動/未応答で制御チャネルに到達できないことを表す(フォールバック用。ADR-0010)。</summary>
public sealed class ControlUnavailableException(string message) : Exception(message);

/// <summary>
/// 制御チャネルのクライアント(CLI verb / 将来の GUI が使う)。1 コマンド = 1 接続の単発往復。
/// サービス未起動(connect timeout)・未応答(read timeout)は <see cref="ControlUnavailableException"/>
/// に正規化し、呼び出し側が破綻せず degrade できるようにする。
/// </summary>
public static class ControlClient
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);

    public static async Task<ControlResponse> SendAsync(
        string command,
        string? pipeName = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var name = pipeName ?? ControlContract.PipeName;
        var connect = connectTimeout ?? DefaultConnectTimeout;
        var read = readTimeout ?? DefaultReadTimeout;

        await using var client = new NamedPipeClientStream(
            ".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync((int)connect.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ControlUnavailableException(
                "サービスが起動していません(制御チャネルに接続できません)");
        }

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(read);

        try
        {
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(
                new ControlRequest { Command = command }, ControlContract.Json);
            await ControlWire.WriteFrameAsync(client, requestBytes, readCts.Token).ConfigureAwait(false);

            var responseBytes = await ControlWire.ReadFrameAsync(client, readCts.Token).ConfigureAwait(false)
                ?? throw new ControlUnavailableException("サービスが応答しません(空応答)");

            return JsonSerializer.Deserialize<ControlResponse>(responseBytes, ControlContract.Json)
                   ?? throw new ControlUnavailableException("サービスの応答を解析できません");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // read 期限切れ(呼び出し元トークンは未発火)= 未応答。
            throw new ControlUnavailableException("サービスが応答しません(タイムアウト)");
        }
        catch (IOException ex)
        {
            throw new ControlUnavailableException($"制御チャネルの通信に失敗しました: {ex.Message}");
        }
    }
}
