using System.IO.Pipes;
using System.Text.Json;

namespace Radirec.Hosting.Control;

/// <summary>
/// 制御チャネルの受け口(ADR-0020 / #179)。トレイのプロセスが起動し、CLI(<see cref="ControlPipeClient"/>)からの
/// 状態取得・設定リロードを <see cref="IServiceControl"/> へつなぐ。取り込みが止まっている(縮退。<paramref name="control"/> が null)
/// ときも起動し、状態取得には「止まっている」と理由を、設定リロードには縮退を返す(トレイの再起動はしない)。
/// <para>
/// 接続ごとに要求 1 つ・応答 1 つ(どちらも JSON 1 行)。受けた接続はすぐ別のタスクへ渡し、次の接続を待つ(遅い要求で
/// 後の接続を止めない。#43 の教訓)。同じユーザーだけに <see cref="PipeOptions.CurrentUserOnly"/> で絞る。
/// </para>
/// </summary>
/// <param name="pipeName">パイプ名。既定は <see cref="ControlProtocol.DefaultPipeName"/>(テストでは一意の名前を渡す)。</param>
/// <param name="control">取り込みワーカーの制御面。縮退なら null。</param>
/// <param name="degradedReason">縮退の理由(W-104 / W-105)。動いていれば null。</param>
/// <param name="configPath">常駐が使っている radirec.json の場所。</param>
/// <param name="trayVersion">トレイのアプリの版。</param>
/// <param name="logWarning">受け口の失敗を残す(gui.log)。</param>
public sealed class ControlPipeServer(
    string pipeName,
    IServiceControl? control,
    string? degradedReason,
    string? configPath,
    string trayVersion,
    Action<string>? logWarning = null) : IAsyncDisposable
{
    /// <summary>要求の 1 行を読むまで待つ時間。繋いだまま何も送らない相手で接続を持ち続けないように。</summary>
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _handlers = [];
    private Task? _acceptLoop;

    public string PipeName { get; } = pipeName;

    /// <summary>接続を待ち始める。すぐ返る(待つのは別のタスク)。</summary>
    public void Start()
    {
        if (_acceptLoop is not null)
            throw new InvalidOperationException("受け口はすでに起動しています。");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                // パイプを作れない・待てない(同じ名前を別のプロセスが持っている等)。受け口だけの失敗で、トレイと取り込みは生かす。
                pipe?.Dispose();
                logWarning?.Invoke($"制御チャネルの受け口で例外(1 秒後にやり直します): {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // 別のタスクへ渡す。直接呼ぶと、要求がすでに届いていたときに同期で処理が進み(設定リロードは同期で重いことがある)、
            // 次の接続を待つのが遅れる。
            var connected = pipe;
            Track(Task.Run(() => HandleAsync(connected, stoppingToken), CancellationToken.None));
        }
    }

    private void Track(Task handler)
    {
        lock (_gate)
            _handlers.Add(handler);
        handler.ContinueWith(t =>
        {
            lock (_gate)
                _handlers.Remove(t);
        }, TaskScheduler.Default);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(pipe, ControlProtocol.LineEncoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, ControlProtocol.LineEncoding, leaveOpen: true) { AutoFlush = true };

                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                readTimeout.CancelAfter(RequestReadTimeout);
                var line = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);

                var response = Respond(line);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, ControlProtocol.JsonOptions).AsMemory(), stoppingToken)
                    .ConfigureAwait(false);
                if (OperatingSystem.IsWindows())
                    pipe.WaitForPipeDrain();
            }
            catch (OperationCanceledException)
            {
                // 停止中、または相手が何も送らなかった。応答せずに切る。
            }
            catch (IOException)
            {
                // 相手が先に切った。
            }
            catch (Exception ex)
            {
                logWarning?.Invoke($"制御チャネルの要求の処理で例外: {ex}");
            }
        }
    }

    /// <summary>要求の 1 行から応答を作る(パイプに触れない)。</summary>
    private ControlResponse Respond(string? line)
    {
        ControlRequest? request = null;
        if (line is not null)
        {
            try
            {
                request = JsonSerializer.Deserialize<ControlRequest>(line, ControlProtocol.JsonOptions);
            }
            catch (JsonException)
            {
                // 下で BadRequest にする。
            }
        }

        if (request is null)
            return Reply(ControlResponseKind.BadRequest, error: "要求を読めませんでした");

        if (request.ProtocolVersion != ControlProtocol.Version)
            return Reply(ControlResponseKind.VersionMismatch,
                error: $"プロトコルの版が違います(トレイ {ControlProtocol.Version} / 要求 {request.ProtocolVersion})");

        return request.Command switch
        {
            ControlProtocol.StatusCommand => Reply(ControlResponseKind.Ok, status: CurrentStatus()),
            ControlProtocol.ReloadCommand => control is null
                ? Reply(ControlResponseKind.Degraded, error: degradedReason ?? "取り込みが止まっています")
                : Reply(ControlResponseKind.Ok, reload: control.RequestReload()),
            _ => Reply(ControlResponseKind.BadRequest, error: $"知らないコマンドです: {request.Command}"),
        };
    }

    private ControlStatus CurrentStatus() => control is null
        ? ControlStatus.Degraded(trayVersion, degradedReason, configPath)
        : ControlStatus.From(control.GetStatus(), configPath);

    private ControlResponse Reply(
        ControlResponseKind kind, string? error = null, ControlStatus? status = null, ReloadResult? reload = null) => new()
    {
        ProtocolVersion = ControlProtocol.Version,
        TrayVersion = trayVersion,
        Kind = kind,
        Error = error,
        Status = status,
        Reload = reload,
    };

    /// <summary>接続を待つのをやめ、処理中の要求が終わるのを待つ。</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        lock (_gate)
            pending = [.. _handlers];
        try
        {
            if (_acceptLoop is not null)
                await _acceptLoop.ConfigureAwait(false);
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logWarning?.Invoke("制御チャネルの要求の処理が 5 秒で終わらないまま止めました");
        }
        _stopping.Dispose();
    }
}
