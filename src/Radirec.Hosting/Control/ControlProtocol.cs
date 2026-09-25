using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radirec.Hosting.Control;

/// <summary>
/// 制御チャネル(CLI → トレイの常駐。ADR-0020 / #179)の名前とメッセージの形。受け口は <see cref="ControlPipeServer"/>、
/// CLI 側は <see cref="ControlPipeClient"/>。1 回の接続で要求 1 つ・応答 1 つを、どちらも UTF-8 の JSON 1 行でやりとりする。
/// </summary>
public static class ControlProtocol
{
    /// <summary>
    /// プロトコルの版。要求・応答の形を変える(フィールドを消す・意味を変える)ときに上げる。アプリの版ではなくこの番号で
    /// 互換を判断する(版が違ってもプロトコルが同じなら動かせる)。
    /// </summary>
    public const int Version = 1;

    /// <summary>名前の先頭。ユーザーごとの名前(<see cref="PipeNameFor"/>)にする。</summary>
    public const string PipeNamePrefix = "radirec-control-";

    public const string StatusCommand = "status";
    public const string ReloadCommand = "reload";

    /// <summary>
    /// 要求・応答の JSON。camelCase・列挙は camelCase の文字列・日本語はエスケープしない。1 行で送るので字下げしない。
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>行の読み書きに使う符号化(BOM を付けない UTF-8)。</summary>
    public static readonly Encoding LineEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 今のユーザーのパイプ名 <c>radirec-control-&lt;ユーザーの SID&gt;</c>(SID が取れない環境ではユーザー名)。
    /// 固定名だと、同じ PC で 2 人がそれぞれトレイを起動したときにぶつかる(#179)。
    /// </summary>
    public static string DefaultPipeName() => PipeNameFor(CurrentUserId());

    /// <summary>ユーザーを表す文字列からパイプ名を作る。パイプ名に使えない文字は <c>_</c> にする。</summary>
    public static string PipeNameFor(string userId)
    {
        var safe = new StringBuilder(userId.Length);
        foreach (var c in userId)
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return PipeNamePrefix + safe;
    }

    private static string CurrentUserId()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.User?.Value is { Length: > 0 } sid)
                    return sid;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // SID が取れなければユーザー名で代える(下)。
            }
        }

        return Environment.UserName;
    }
}

/// <summary>CLI → 常駐の要求。</summary>
/// <param name="ProtocolVersion">送り手のプロトコルの版(<see cref="ControlProtocol.Version"/>)。</param>
/// <param name="Command"><see cref="ControlProtocol.StatusCommand"/> か <see cref="ControlProtocol.ReloadCommand"/>。</param>
public sealed record ControlRequest(int ProtocolVersion, string Command);

/// <summary>応答の種類。</summary>
public enum ControlResponseKind
{
    /// <summary>要求を処理した(状態取得の結果、または設定リロードの受理 / 拒否)。</summary>
    Ok,

    /// <summary>プロトコルの版が合わない。何もしていない。</summary>
    VersionMismatch,

    /// <summary>取り込みが止まっている(縮退)ので、頼む操作はできない。理由は <see cref="ControlResponse.Error"/>。</summary>
    Degraded,

    /// <summary>要求を読めない・知らないコマンド。何もしていない。</summary>
    BadRequest,
}

/// <summary>常駐 → CLI の応答。</summary>
public sealed record ControlResponse
{
    /// <summary>応答側のプロトコルの版。</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>応答側(トレイ)のアプリの版。版が合わないときの知らせに使う。</summary>
    public required string TrayVersion { get; init; }

    public required ControlResponseKind Kind { get; init; }

    /// <summary><see cref="ControlResponseKind.Ok"/> 以外の理由。</summary>
    public string? Error { get; init; }

    /// <summary>状態取得の結果(<c>status</c> のときだけ)。</summary>
    public ControlStatus? Status { get; init; }

    /// <summary>設定リロードの結果(<c>reload</c> で <see cref="ControlResponseKind.Ok"/> のときだけ)。</summary>
    public ReloadResult? Reload { get; init; }
}

/// <summary>
/// 状態取得の結果。<see cref="ServiceStatusSnapshot"/> の項目に、常駐の設定ファイルの場所と縮退の理由を足したもの(#179)。
/// 取り込みが止まっている(縮退)ときは <see cref="Running"/> が false で、スナップショット由来の項目は空。
/// </summary>
public sealed record ControlStatus
{
    /// <summary>取り込みが動いているか。</summary>
    public required bool Running { get; init; }

    /// <summary>取り込みが止まっている理由(W-104 / W-105 の文言)。動いていれば null。</summary>
    public string? DegradedReason { get; init; }

    /// <summary>トレイのアプリの版。</summary>
    public required string Version { get; init; }

    /// <summary>取り込みワーカーの起動時刻(UTC)。縮退なら null。</summary>
    public DateTimeOffset? StartedUtc { get; init; }

    /// <summary>常駐が使っている設定ファイル(radirec.json)の場所。</summary>
    public string? ConfigPath { get; init; }

    /// <summary>監視ルート。縮退なら null。</summary>
    public string? RadioRoot { get; init; }

    /// <summary>ファイルの変化を監視しているか(false = 定期スキャンのみ、または縮退)。</summary>
    public bool Watching { get; init; }

    /// <summary>最後にスキャンを終えた時刻(UTC)。未走査・縮退なら null。</summary>
    public DateTimeOffset? LastScanCompletedUtc { get; init; }

    /// <summary>最後のスキャンのきっかけ。未走査・縮退なら null。</summary>
    public string? LastScanTrigger { get; init; }

    public int LastImported { get; init; }

    public int LastFailed { get; init; }

    /// <summary>放送局マスタ(stations.json)の場所。縮退なら null。</summary>
    public string? StationsFile { get; init; }

    /// <summary>番組の設定(programs.json)の場所。縮退なら null。</summary>
    public string? ProgramsFile { get; init; }

    /// <summary>取り込みワーカーのスナップショットから作る。</summary>
    public static ControlStatus From(ServiceStatusSnapshot snapshot, string? configPath) => new()
    {
        Running = snapshot.Running,
        Version = snapshot.Version,
        StartedUtc = snapshot.StartedUtc,
        ConfigPath = configPath,
        RadioRoot = snapshot.RadioRoot,
        Watching = snapshot.Watching,
        LastScanCompletedUtc = snapshot.LastScanCompletedUtc,
        LastScanTrigger = snapshot.LastScanTrigger,
        LastImported = snapshot.LastImported,
        LastFailed = snapshot.LastFailed,
        StationsFile = snapshot.StationsFile,
        ProgramsFile = snapshot.ProgramsFile,
    };

    /// <summary>取り込みが止まっている(縮退)ときの状態。</summary>
    public static ControlStatus Degraded(string trayVersion, string? degradedReason, string? configPath) => new()
    {
        Running = false,
        DegradedReason = degradedReason,
        Version = trayVersion,
        ConfigPath = configPath,
    };
}
