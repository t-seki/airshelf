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
    public void Show(IngestSummaryPayload payload)
    {
        var (title, body) = IngestToastFormatter.Format(payload);

        new ToastContentBuilder()
            .AddText(title)
            .AddText(body)
            .Show();
    }
}
