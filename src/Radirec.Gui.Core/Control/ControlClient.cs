using System.IO.Pipes;
using System.Text.Json;

namespace Radirec.Gui.Core.Control;

/// <summary>サービスが未起動/未応答で制御チャネルに到達できないことを表す(縮退用。ADR-0010)。</summary>
public sealed class ControlUnavailableException(string message) : Exception(message);

/// <summary>設定リロード要求の結果(受け手側)。<c>accepted ≠ 反映完了</c>(ADR-0010)。</summary>
public sealed record ReloadOutcome(bool Accepted, string? Error)
{
    public static ReloadOutcome Ok() => new(true, null);
    public static ReloadOutcome Failed(string error) => new(false, error);
}

/// <summary>
/// 制御チャネルのクライアント(利用者 GUI 側。ADR-0010)。発行側 <c>Radirec.Hosting.Control.ControlClient</c>
/// の型は共有せず、同じ wire 契約に自前で追従する。1 コマンド = 1 接続の単発往復。サービス未起動
/// (connect timeout)・未応答(read timeout)は <see cref="ControlUnavailableException"/> に正規化し、
/// トレイが縮退(グレーアウト)できるようにする。<see cref="NamedPipeClientStream"/> はクロスプラット
/// フォームなので WSL でも往復テストできる。
/// </summary>
public static class ControlClient
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);

    // reload はサーバ側で同期 config 読込+検証を行い、応答 write を read 期限とは別枠にして意図的に
    // 「非境界」にしている(ControlPipeServer.HandleConnectionAsync)。クライアントが既定 5s で縛ると、
    // 受理済みだが処理が長い reload を「未応答」と誤報するため、reload だけ read 期限を延ばす(ADR-0010)。
    private static readonly TimeSpan ReloadReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>手動再スキャンを要求する(受理まで)。サービス未達なら <see cref="ControlUnavailableException"/>。</summary>
    public static async Task RequestRescanAsync(
        string? pipeName = null, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(ControlContract.Commands.Rescan, pipeName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!response.Ok)
            throw new ControlUnavailableException(response.Error ?? "手動再スキャンを受理できませんでした");
    }

    /// <summary>ライブ状態を取得する。サービス未達なら <see cref="ControlUnavailableException"/>。</summary>
    public static async Task<ServiceStatusSnapshot> GetStatusAsync(
        string? pipeName = null, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(ControlContract.Commands.Status, pipeName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!response.Ok)
            throw new ControlUnavailableException(response.Error ?? "状態を取得できませんでした");

        return response.ResultAs<ServiceStatusSnapshot>()
               ?? throw new ControlUnavailableException("状態応答を解析できません");
    }

    /// <summary>
    /// 設定リロードを要求する(サービス実質再起動相当)。サービス未達なら
    /// <see cref="ControlUnavailableException"/>、config 検証失敗等は <see cref="ReloadOutcome.Accepted"/>=false。
    /// </summary>
    public static async Task<ReloadOutcome> RequestReloadAsync(
        string? pipeName = null, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                ControlContract.Commands.Reload, pipeName,
                readTimeout: ReloadReadTimeout, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Ok ? ReloadOutcome.Ok() : ReloadOutcome.Failed(response.Error ?? "設定リロードに失敗しました");
    }

    /// <summary>1 コマンドを送って応答を受ける単発往復。到達不能は正規化して投げる。</summary>
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
            throw new ControlUnavailableException("サービスが起動していません(制御チャネルに接続できません)");
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
            throw new ControlUnavailableException("サービスが応答しません(タイムアウト)");
        }
        catch (IOException ex)
        {
            // EndOfStreamException(途中切断)を含む。応答フレームの読み取り失敗。
            throw new ControlUnavailableException($"制御チャネルの通信に失敗しました: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            // フレーム長が不正(ControlWire)。壊れた/別プロトコルの相手。
            throw new ControlUnavailableException($"制御メッセージのフレームが不正です: {ex.Message}");
        }
        catch (JsonException ex)
        {
            // 応答ボディが ControlResponse JSON でない(空フレーム・破損・別プロトコル)。
            // ここで正規化しないと async void のトレイハンドラを突き抜けてトレイが落ちる。
            throw new ControlUnavailableException($"サービスの応答を解析できません: {ex.Message}");
        }
    }
}
