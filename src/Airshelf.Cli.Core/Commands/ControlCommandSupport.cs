using Airshelf.Cli.Core.Output;
using Airshelf.Hosting.Control;

namespace Airshelf.Cli.Core.Commands;

/// <summary>
/// 常駐に頼むコマンド(<c>status</c> / <c>reload</c> / <c>ingest</c> / <c>reapply</c>)に共通の、頼めなかったときの扱い(#179 / #180 / #181)。
/// </summary>
internal static class ControlCommandSupport
{
    /// <summary>トレイと CLI でプロトコルの版が違うときの知らせ(#179 の設計の決定の文言)。</summary>
    public static string VersionMismatchMessage(ControlCallResult result, CliVersion cli) =>
        $"トレイと CLI の版が違います(トレイ v{result.TrayVersion ?? "?"} / CLI v{cli.Value})";

    /// <summary>
    /// 応答が得られなかった結果(繋がらない・版の違い・読めない)の終了コードと理由。応答が得られていれば null。
    /// 繋がったが応答を読めない(途中で切れた・時間切れ)ときも、頼んだ結果を常駐から得られなかったので 2 にする。
    /// <c>ingest</c> / <c>reapply</c> の時間切れ(1 時間)は常駐が止まっているとは限らず、走査・再適用がトレイで続いていることがある。
    /// 理由(<see cref="ControlCallResult.Error"/>)でそう伝え、<c>airshelf status</c> / gui.log で確かめるよう促す(usage.md の終了コードの表)。
    /// </summary>
    public static (int ExitCode, string Message)? Unanswered(ControlCallResult result, CliVersion cli) => result.Outcome switch
    {
        ControlCallOutcome.NotRunning => (ExitCodes.ResidentNotRunning, result.Error ?? ControlPipeClient.NotRunningMessage),
        ControlCallOutcome.VersionMismatch => (ExitCodes.UsageOrConfig, VersionMismatchMessage(result, cli)),
        ControlCallOutcome.Failed => (ExitCodes.ResidentNotRunning, result.Error ?? "常駐の応答を読めませんでした"),
        _ => null,
    };
}
