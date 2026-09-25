using System.IO.Pipes;
using System.Text.Json;

namespace Radirec.Hosting.Control;

/// <summary>制御チャネルへ頼んだ結果の種類。</summary>
public enum ControlCallOutcome
{
    /// <summary>常駐が応答した(中身は <see cref="ControlCallResult.Response"/>。縮退・拒否もここに入る)。</summary>
    Answered,

    /// <summary>常駐に繋がらない(トレイが起動していない)。</summary>
    NotRunning,

    /// <summary>トレイと CLI でプロトコルの版が違う。</summary>
    VersionMismatch,

    /// <summary>繋がったが、応答を読めなかった(途中で切れた・時間切れ・形が違う)。</summary>
    Failed,
}

/// <summary>制御チャネルへ頼んだ結果。</summary>
/// <param name="Response">常駐の応答(<see cref="ControlCallOutcome.Answered"/> と、版が違うときに応答があればそれ)。</param>
/// <param name="Error">応答が得られなかった・使えない理由。</param>
/// <param name="TrayVersion">応答したトレイのアプリの版(分かれば)。版が違うときの知らせに使う。</param>
public sealed record ControlCallResult(
    ControlCallOutcome Outcome, ControlResponse? Response, string? Error, string? TrayVersion = null);

/// <summary>CLI が制御チャネルへ頼む口。テストでは差し替える。</summary>
public interface IControlClient
{
    Task<ControlCallResult> SendAsync(string command, CancellationToken cancellationToken);
}

/// <summary>
/// 制御チャネルの CLI 側(ADR-0020 / #179)。受け口(<see cref="ControlPipeServer"/>)へ繋ぎ、要求を 1 行送って応答を 1 行読む。
/// 2 秒で繋がらなければ「常駐が動いていない」とする(#179 の設計の決定)。
/// </summary>
/// <param name="scanResponseTimeout">走査の実行(<c>scan</c>)の応答を待つ時間。既定は <see cref="DefaultScanResponseTimeout"/>。</param>
public sealed class ControlPipeClient(
    string pipeName, TimeSpan? connectTimeout = null, TimeSpan? responseTimeout = null, TimeSpan? scanResponseTimeout = null)
    : IControlClient
{
    /// <summary>接続を諦めるまでの時間(#179 の設計の決定)。</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>応答を待つ時間。状態取得・設定リロードの受理はどちらも速いので、これを超えたら異常とみなす。</summary>
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 走査の実行の応答を待つ時間(#180)。走査が終わるまで応答が来ない(取り込み中に頼めば、その走査も待つ)。溜まった録音を
    /// NAS へ書き出すと長くかかるので長めにとり、それでも終わらなければ、常駐が止まっているとみなして諦める(走査は常駐で続く)。
    /// </summary>
    public static readonly TimeSpan DefaultScanResponseTimeout = TimeSpan.FromHours(1);

    /// <summary>今のユーザーの既定のパイプ(<see cref="ControlProtocol.DefaultPipeName"/>)へ繋ぐ。</summary>
    public ControlPipeClient()
        : this(ControlProtocol.DefaultPipeName())
    {
    }

    public string PipeName { get; } = pipeName;

    public Task<ControlCallResult> SendAsync(string command, CancellationToken cancellationToken) =>
        SendAsync(new ControlRequest(ControlProtocol.Version, command), cancellationToken);

    /// <summary>要求をそのまま送る(版の違う要求を送るテストにも使う)。</summary>
    public async Task<ControlCallResult> SendAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.ConnectAsync(connectTimeout ?? DefaultConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return new ControlCallResult(ControlCallOutcome.NotRunning, null, NotRunningMessage);
        }

        string? line;
        try
        {
            using var reader = new StreamReader(pipe, ControlProtocol.LineEncoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, ControlProtocol.LineEncoding, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, ControlProtocol.JsonOptions).AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Command == ControlProtocol.ScanCommand
                ? scanResponseTimeout ?? DefaultScanResponseTimeout
                : responseTimeout ?? DefaultResponseTimeout);
            line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ControlCallResult(ControlCallOutcome.Failed, null, request.Command == ControlProtocol.ScanCommand
                ? "走査が時間内に終わりませんでした(常駐では続いている場合があります。status で確かめてください)"
                : "常駐から応答がありません(時間切れ)");
        }
        catch (IOException ex)
        {
            return new ControlCallResult(ControlCallOutcome.Failed, null, $"常駐との接続が切れました: {ex.Message}");
        }

        if (line is null)
            return new ControlCallResult(ControlCallOutcome.Failed, null, "常駐の応答を読めませんでした");

        // 先に版だけを読む。版の違う応答は今の形に読めないことがあるので、形を読む前に版の違いとして返す。
        var (version, trayVersion) = ReadVersions(line);
        if (version is { } v && v != ControlProtocol.Version)
            return new ControlCallResult(ControlCallOutcome.VersionMismatch, null,
                $"プロトコルの版が違います(トレイ {v} / CLI {ControlProtocol.Version})", trayVersion);

        ControlResponse? response = null;
        try
        {
            response = JsonSerializer.Deserialize<ControlResponse>(line, ControlProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            // 下で Failed にする。
        }

        if (response is null)
            return new ControlCallResult(ControlCallOutcome.Failed, null, "常駐の応答を読めませんでした");

        if (response.Kind == ControlResponseKind.VersionMismatch || response.ProtocolVersion != ControlProtocol.Version)
            return new ControlCallResult(ControlCallOutcome.VersionMismatch, response, response.Error, response.TrayVersion);

        return new ControlCallResult(ControlCallOutcome.Answered, response, null, response.TrayVersion);
    }

    private static (int? ProtocolVersion, string? TrayVersion) ReadVersions(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);
            int? protocol = doc.RootElement.TryGetProperty("protocolVersion", out var v) && v.TryGetInt32(out var n) ? n : null;
            var tray = doc.RootElement.TryGetProperty("trayVersion", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            return (protocol, tray);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>常駐に繋がらないときの知らせ(#179 の設計の決定の文言)。</summary>
    public const string NotRunningMessage = "常駐が動いていません(Radirec.Gui.exe を起動してください)";
}
