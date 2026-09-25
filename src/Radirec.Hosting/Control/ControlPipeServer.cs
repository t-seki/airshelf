using System.IO.Pipes;
using System.Text.Json;

namespace Radirec.Hosting.Control;

/// <summary>
/// 制御チャネルの受け口(ADR-0020 / #179)。トレイのプロセスが起動し、CLI(<see cref="ControlPipeClient"/>)からの
/// 状態取得・設定リロード・走査の実行(#180)を <see cref="IServiceControl"/> へつなぐ。取り込みが止まっている(縮退。<paramref name="control"/> が null)
/// ときも起動し、状態取得には「止まっている」と理由を、設定リロード・走査の実行には縮退を返す(トレイの再起動はしない)。
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

    /// <summary>応答を書いた後、相手が読んで切るまで待つ時間。過ぎたら切る。</summary>
    private static readonly TimeSpan ResponseReadTimeout = TimeSpan.FromSeconds(5);

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

    /// <summary>受け口の失敗が続くときの、最初の待ち時間と最大の待ち時間(倍々に延ばす)。</summary>
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    private async Task AcceptLoopAsync(CancellationToken stoppingToken)
    {
        var retryDelay = FirstRetryDelay;
        string? lastError = null;
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
                // 失敗が続くときは待ち時間を倍々に延ばし(最大 1 分)、同じ理由が続く間は gui.log に書かない。
                pipe?.Dispose();
                if (ex.Message != lastError)
                    logWarning?.Invoke($"制御チャネルの受け口で例外(やり直します。同じ理由が続く間は書きません): {ex.Message}");
                lastError = ex.Message;
                try { await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
                continue;
            }

            retryDelay = FirstRetryDelay;
            lastError = null;

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

                // 走査の実行は走査が終わるまで応答しない(相手はその間、待ち続ける)。停止するときは待つのをやめて応答せずに切る。
                var response = await RespondAsync(line, stoppingToken).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, ControlProtocol.JsonOptions).AsMemory(), stoppingToken)
                    .ConfigureAwait(false);

                // 相手が応答を読んで切るのを待ってから閉じる(時間切れつき)。WaitForPipeDrain は時間切れが無く、応答を読まない相手で
                // 処理が止まったままになりうるので使わない。書いてすぐ閉じると、Windows で相手が読む前に応答が消えないかを
                // 確かめられていないので、相手が切る(読み終えて閉じる)まで待つ形にした。
                using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                closeTimeout.CancelAfter(ResponseReadTimeout);
                await reader.ReadLineAsync(closeTimeout.Token).ConfigureAwait(false);
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
    private async Task<ControlResponse> RespondAsync(string? line, CancellationToken stoppingToken)
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
            ControlProtocol.ReloadCommand => StoppedReason() is { } reason
                ? Reply(ControlResponseKind.Degraded, error: reason)
                : Reply(ControlResponseKind.Ok, reload: control!.RequestReload()),
            ControlProtocol.ScanCommand => StoppedReason() is { } reason
                ? Reply(ControlResponseKind.Degraded, error: reason)
                : Reply(ControlResponseKind.Ok,
                    scanRun: await control!.RunScanAsync().WaitAsync(stoppingToken).ConfigureAwait(false)),
            _ => Reply(ControlResponseKind.BadRequest, error: $"知らないコマンドです: {request.Command}"),
        };
    }

    /// <summary>
    /// ワーカーは起動したが、その後に止まったときの理由(トレイの W-103 と同じ趣旨)。自己更新の適用前にワーカーを止めたとき・
    /// 取り込みのループが異常終了したとき。
    /// </summary>
    public const string WorkerStoppedReason = "取り込みが止まりました(異常終了か更新の適用中。gui.log 参照 → 再起動)";

    /// <summary>
    /// 取り込みが動いていなければその理由、動いていれば null。ワーカーが無い(縮退)ときに加え、ワーカーはあるが止まった
    /// (<see cref="ServiceStatusSnapshot.Running"/> が false)ときも止まっているとみなす。
    /// </summary>
    private string? StoppedReason()
    {
        if (control is null)
            return degradedReason ?? "取り込みが止まっています";
        return control.GetStatus().Running ? null : WorkerStoppedReason;
    }

    private ControlStatus CurrentStatus()
    {
        if (control is null)
            return ControlStatus.Degraded(trayVersion, degradedReason, configPath);
        var snapshot = control.GetStatus();
        var status = ControlStatus.From(snapshot, configPath);
        return snapshot.Running ? status : status with { DegradedReason = WorkerStoppedReason };
    }

    private ControlResponse Reply(
        ControlResponseKind kind, string? error = null, ControlStatus? status = null, ReloadResult? reload = null,
        ScanRunResult? scanRun = null) => new()
    {
        ProtocolVersion = ControlProtocol.Version,
        TrayVersion = trayVersion,
        Kind = kind,
        Error = error,
        Status = status,
        Reload = reload,
        ScanRun = scanRun,
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
