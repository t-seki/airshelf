using System.Globalization;
using Radirec.Hosting.Control;

namespace Radirec.Gui.Core;

/// <summary>
/// 取り込みの状態を 2 行の文言にしたもの(状態 / 最終スキャン)と、制御(再スキャン等)を受け付けられるか。
/// </summary>
/// <param name="Status">W-101〜W-105・W-153(<c>状態: …</c>)。</param>
/// <param name="LastScan">W-106〜W-108(<c>最終スキャン: …</c>)。</param>
/// <param name="CanControl">取り込みが動いていて、再スキャン等を受け付けられるか。</param>
public sealed record ServiceStatusView(string Status, string LastScan, bool CanControl);

/// <summary>
/// 取り込みの状態の文言(docs/mockups/management-window.md の W-101〜W-108・W-153)。トレイのメニュー(W-604)と
/// 管理画面のホームが同じ文言を出すので、ここに 1 つだけ置く(#151)。
/// </summary>
public static class ServiceStatusText
{
    /// <summary>
    /// 設定は読めたが、取り込みワーカーを起動できなかったときの理由(W-104 の括弧の中)。原因と、直して戻る導線は
    /// 管理画面の設定ページの先頭に出す(W-406〜W-409。#168)。
    /// </summary>
    public const string DegradedStartFailed = "取り込みの起動に失敗しました(設定ページ参照)";

    /// <summary><c>radirec.json</c> を読めなかったときの理由(W-105 の括弧の中)。</summary>
    public const string DegradedConfigUnreadable = "設定を読み込めませんでした";

    /// <summary>
    /// W-153。ワーカーは作れたが、起動処理(孤児の掃除・監視の開始)が失敗して止まったとき(#217)。W-104(ワーカーを作れなかった)と
    /// 違い、設定ページには原因が出ないので gui.log へ案内する。
    /// </summary>
    public const string StartLoopFailed = "状態: 停止(取り込みの起動処理に失敗。gui.log 参照 → 原因を直して再起動)";

    /// <summary>W-108。取り込みが止まっているときの最終スキャン。</summary>
    public const string LastScanUnknown = "最終スキャン: —";

    /// <summary>
    /// 取り込みワーカーが無い(縮退)ときの文言。W-104 / W-105:<c>状態: 取り込み停止中({理由})</c> + W-108。
    /// </summary>
    public static ServiceStatusView Stopped(string? degradedReason) =>
        new($"状態: 取り込み停止中({degradedReason ?? "未起動"})", LastScanUnknown, CanControl: false);

    /// <summary>
    /// ワーカーのスナップショットからの文言。<paramref name="zone"/> は最終スキャンの時刻を表示する時間帯
    /// (既定はローカル。テストで固定するため差し替えられる)。
    /// </summary>
    public static ServiceStatusView From(ServiceStatusSnapshot status, TimeZoneInfo? zone = null)
    {
        // ワーカーが異常終了してもトレイ(メッセージループ)は生存しうる。その場合「稼働中」と偽らず、
        // 停止を明示して制御を受け付けない(誤って「動いている」と信じさせない。ADR-0012)。
        // 起動処理(掃除・監視の開始)で止まったときは、異常終了と分けて出す(#217)。原因(読めないフォルダ等)を直さないと、
        // 再起動しても同じところで止まるため。
        if (!status.Running)
            return status.StartFailure is null
                ? new("状態: 停止(取り込みが異常終了。gui.log 参照 → 再起動)", LastScanUnknown, CanControl: false) // W-103 + W-108
                : new(StartLoopFailed, LastScanUnknown, CanControl: false); // W-153 + W-108

        var line = status.Watching ? "状態: 稼働中(監視中)" : "状態: 稼働中(定期スキャンのみ)"; // W-101 / W-102
        return new(line, LastScan(status, zone), CanControl: true);
    }

    // W-106 / W-107。きっかけ(W-109)はワーカーが書いた語をそのまま出す。
    private static string LastScan(ServiceStatusSnapshot status, TimeZoneInfo? zone)
    {
        if (status.LastScanCompletedUtc is not { } completed)
            return "最終スキャン: 未走査"; // W-107

        // "M/d" の "/" はカルチャの日付区切りに置き換わるので、InvariantCulture で固定する。
        var local = TimeZoneInfo.ConvertTime(completed, zone ?? TimeZoneInfo.Local)
            .ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        var trigger = string.IsNullOrEmpty(status.LastScanTrigger) ? "" : $" {status.LastScanTrigger}";
        var failed = status.LastFailed > 0 ? $"・失敗 {status.LastFailed}" : "";
        return $"最終スキャン: {local}{trigger}(取込 {status.LastImported}{failed})";
    }
}
