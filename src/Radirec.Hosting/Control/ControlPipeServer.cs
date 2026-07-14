using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Radirec.Hosting.Control;

/// <summary>
/// 制御チャネルの named pipe サーバ(ADR-0010)。1 コマンド = 1 接続の単発往復を直列に捌く。
/// 1 接続の処理で例外が出てもその接続を閉じるだけで accept ループを継続し、1 クライアントの不正が
/// 本体取込を巻き込まない。認可は Windows でのみ <see cref="PipeSecurity"/> ACL を適用する
/// (round-trip 自体はクロスプラットフォームで検証可能なよう ACL 適用を隔離する)。
/// </summary>
public sealed class ControlPipeServer(
    IServiceControl control,
    string? pipeName = null,
    Action<Exception>? onConnectionError = null,
    TimeSpan? readTimeout = null)
{
    // 接続だけして送ってこないクライアントで accept ループを固めないための read 期限(テストで短縮可)。
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);

    // パイプ生成の一過性失敗(Windows の "all pipe instances busy" 等)からの回復待ち。
    private static readonly TimeSpan CreateRetryDelay = TimeSpan.FromSeconds(1);

    private readonly string _pipeName = pipeName ?? ControlContract.PipeName;
    private readonly TimeSpan _readTimeout = readTimeout ?? DefaultReadTimeout;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServer();
            }
            catch (Exception ex)
            {
                // パイプ生成の失敗(dispose→再生成の窓での一過性 busy 等)で listen を永久停止しない。
                // 報告して短い backoff 後に再試行する(「1 接続の失敗で listen を止めない」基準の徹底)。
                onConnectionError?.Invoke(ex);
                try
                {
                    await Task.Delay(CreateRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleConnectionAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return; // 停止要求のみ正常終了。read 期限切れ(下の catch)と区別する。
            }
            catch (Exception ex)
            {
                // 1 接続の処理失敗(read タイムアウト = 停止トークン未発火の OCE を含む)は listen を止めない。
                onConnectionError?.Invoke(ex);
            }
            finally
            {
                server.Dispose();
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(_readTimeout);

        var requestBytes = await ControlWire.ReadFrameAsync(server, readCts.Token).ConfigureAwait(false);
        if (requestBytes is null)
            return; // 何も送らず切断。応答不要。

        var response = Dispatch(requestBytes);
        var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, ControlContract.Json);
        await ControlWire.WriteFrameAsync(server, responseBytes, readCts.Token).ConfigureAwait(false);
    }

    private ControlResponse Dispatch(byte[] requestBytes)
    {
        ControlRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ControlRequest>(requestBytes, ControlContract.Json);
        }
        catch (JsonException ex)
        {
            return ControlResponse.Failure($"要求 JSON を解析できません: {ex.Message}");
        }

        if (request is null)
            return ControlResponse.Failure("空の要求です");
        if (request.SchemaVersion != ControlContract.SchemaVersion)
            return ControlResponse.Failure(
                $"未対応の schemaVersion={request.SchemaVersion}(対応 {ControlContract.SchemaVersion})");

        switch (request.Command)
        {
            case ControlContract.Commands.Rescan:
                control.RequestRescan();
                return ControlResponse.Success(new { queued = true });

            case ControlContract.Commands.Status:
                return ControlResponse.Success(control.GetStatus());

            default:
                return ControlResponse.Failure($"未知のコマンド: {request.Command}");
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        // 認可 ACL は Windows 専用。非 Windows(WSL 開発/CI)は ACL 無しで round-trip だけ成立させる。
        if (OperatingSystem.IsWindows())
            return CreateSecuredServer(_pipeName);

        return new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    // 既定インストールではサービス = 操作者(install.ps1 の -ServiceAccount 既定 .\$USERNAME)なので
    // 自 SID(実行中プロセスのユーザー)への許可で「ローカル同一ユーザー限定」が成立する(ADR-0010)。
    // split-privilege(専用サービスアカウント)では操作者 SID を別途許可する必要がある(#40 で対応)。
    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateSecuredServer(string pipeName)
    {
        var security = new PipeSecurity();
        var self = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("現在のユーザー SID を取得できません");
        security.AddAccessRule(new PipeAccessRule(
            self,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: 0, security);
    }
}
