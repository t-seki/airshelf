using System.ComponentModel;
using Airshelf.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Airshelf.Cli.Core;

/// <summary>すべてのコマンドに共通のオプション。</summary>
public class CliSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("結果を JSON で標準出力へ 1 つだけ出す(表や色付きの行は出さない)。形は usage の「JSON と終了コード」")]
    public bool Json { get; init; }
}

/// <summary>コマンドに共通の、結果が出る前に止まるときの扱い。</summary>
internal static class CommandSupport
{
    /// <summary>
    /// 設定を読む。読めなければ理由を出して(<c>--json</c> なら JSON で)null を返す。呼び出し側は
    /// <see cref="ExitCodes.UsageOrConfig"/> で終わる。
    /// </summary>
    public static AirshelfConfig? LoadConfig(ICliConfigSource source, IAnsiConsole console, string command, bool json)
    {
        if (source.TryLoad(out var config, out var error) && config is not null)
            return config;

        Fail(console, command, json, ExitCodes.UsageOrConfig, error ?? "設定を読めませんでした");
        return null;
    }

    /// <summary>結果が出る前に止まったことを出す(表なら赤い 1 行、<c>--json</c> なら <see cref="CliJson.Failure"/>)。</summary>
    public static int Fail(IAnsiConsole console, string command, bool json, int exitCode, string message)
    {
        if (json)
            CliJson.Write(console, CliJson.Failure(command, exitCode, message));
        else
            console.MarkupLineInterpolated($"[red]{message}[/]");
        return exitCode;
    }
}
