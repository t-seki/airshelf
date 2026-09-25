using Radirec.Cli.Core.Output;
using Radirec.Hosting.Control;

namespace Radirec.Cli.Core.Commands;

/// <summary>常駐に頼むコマンド(<c>status</c> / <c>reload</c> / <c>ingest</c>)に共通の、頼めなかったときの扱い(#179 / #180)。</summary>
internal static class ControlCommandSupport
{
    /// <summary>トレイと CLI でプロトコルの版が違うときの知らせ(#179 の設計の決定の文言)。</summary>
    public static string VersionMismatchMessage(ControlCallResult result, CliVersion cli) =>
        $"トレイと CLI の版が違います(トレイ v{result.TrayVersion ?? "?"} / CLI v{cli.Value})";

    /// <summary>
    /// 応答が得られなかった結果(繋がらない・版の違い・読めない)の終了コードと理由。応答が得られていれば null。
    /// 繋がったが応答を読めない(途中で切れた・時間切れ)ときは、常駐が正しく応答していないので 2 にする。
    /// </summary>
    public static (int ExitCode, string Message)? Unanswered(ControlCallResult result, CliVersion cli) => result.Outcome switch
    {
        ControlCallOutcome.NotRunning => (ExitCodes.ResidentNotRunning, result.Error ?? ControlPipeClient.NotRunningMessage),
        ControlCallOutcome.VersionMismatch => (ExitCodes.UsageOrConfig, VersionMismatchMessage(result, cli)),
        ControlCallOutcome.Failed => (ExitCodes.ResidentNotRunning, result.Error ?? "常駐の応答を読めませんでした"),
        _ => null,
    };
}
