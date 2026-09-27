using System.Runtime.Versioning;
using CommunityToolkit.WinUI.Notifications;
using Radirec.Gui.Core;

namespace Radirec.Gui;

/// <summary>
/// 取込要約をネイティブトーストで表示する(ADR-0009)。<see cref="ToastNotificationManagerCompat"/> は
/// 非パッケージ Win32 でも動き、初回 Show 時に AUMID + スタートメニューショートカットを自動生成する。
/// 表示専用(タップ activation は実装しない → COM activator 不要)。実機で検証すること。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ToastPresenter
{
    /// <summary>取込通知(「新規取込 N件」)を表示する。起動時 catch-up の集約もこれで出す(ADR-0012)。</summary>
    public void Show(IngestSummaryPayload payload) =>
        Show(IngestToastFormatter.BuildTexts(payload));

    /// <summary>取込失敗通知(「取込失敗 N件」)を表示する(ADR-0011)。</summary>
    public void ShowFailure(IngestFailurePayload payload) =>
        Show(IngestToastFormatter.BuildFailureTexts(payload));

    /// <summary>保留の通知(「判断待ち N件」)を表示する(ADR-0021)。</summary>
    public void ShowHeld(IngestHeldPayload payload) =>
        Show(IngestToastFormatter.BuildHeldTexts(payload));

    private static void Show(IReadOnlyList<string> texts)
    {
        // 要素数上限への切り詰めは formatter(テスト可能な純粋関数)に集約済み。ここは
        // 各要素を <text> として AddText するだけ(WinRT の <text> は埋め込み改行を折り返さない)。
        var builder = new ToastContentBuilder();
        foreach (var text in texts)
            builder.AddText(text);

        builder.Show();
    }
}
