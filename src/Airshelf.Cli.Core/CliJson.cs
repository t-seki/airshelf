using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Airshelf.Cli.Core;

/// <summary>
/// <c>--json</c> の出力(ADR-0020)。どのコマンドも標準出力へ JSON を 1 つだけ出し、最上位の形は同じ
/// (<see cref="CliEnvelope"/>)。形を変えるときは <see cref="SchemaVersion"/> を上げる(エージェントの手順が黙って壊れないように)。
/// </summary>
public static class CliJson
{
    /// <summary>JSON の形の版。フィールドを消す・意味を変えるときに上げる(足すだけなら上げない)。</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// camelCase・列挙は camelCase の文字列・日本語はエスケープしない(エージェントもそのまま読める)。null のフィールドも出す
    /// (形を一定に保つ)。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string Serialize(CliEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);

    /// <summary>
    /// JSON を 1 つ書く。Spectre の markup を通さず、出力先の writer へそのまま書く(<c>[</c> などを markup と取り違えない)。
    /// </summary>
    public static void Write(IAnsiConsole console, CliEnvelope envelope)
    {
        var writer = console.Profile.Out.Writer;
        writer.WriteLine(Serialize(envelope));
        writer.Flush();
    }

    /// <summary>成功・一部失敗など、結果が出たときの JSON。</summary>
    public static CliEnvelope Result(string command, int exitCode, object result) =>
        new(SchemaVersion, command, exitCode, Error: null, Result: result);

    /// <summary>結果が出る前に止まったとき(設定を読めない・使い方の誤りなど)の JSON。</summary>
    public static CliEnvelope Failure(string? command, int exitCode, string error) =>
        new(SchemaVersion, command, exitCode, error, Result: null);
}

/// <summary>
/// <c>--json</c> の最上位の形。どのコマンドでも同じ。
/// </summary>
/// <param name="SchemaVersion">JSON の形の版(<see cref="CliJson.SchemaVersion"/>)。</param>
/// <param name="Command">
/// コマンド名(<c>ingest</c> など)。コマンドが決まっていれば、解析の誤り・実行中の例外でも入る。知らないコマンドなどで決まらなければ null。
/// </param>
/// <param name="ExitCode">プロセスの終了コード(<see cref="ExitCodes"/>)。</param>
/// <param name="Error">
/// 結果が出る前に止まった理由。結果が出たときは null。例外は、再適用で予定が変わった(終了コード 3。#181)とき:実行しなかった理由と、
/// 新しい予定の結果の両方が入る。
/// </param>
/// <param name="Result">コマンドごとの結果。止まったときは null。</param>
public sealed record CliEnvelope(int SchemaVersion, string? Command, int ExitCode, string? Error, object? Result);
