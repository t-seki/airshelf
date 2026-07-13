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
        // 要素数上限への切り詰めは BuildTexts(テスト可能な純粋関数)に集約済み。ここは
        // 各要素を <text> として AddText するだけ(WinRT の <text> は埋め込み改行を折り返さない)。
        var builder = new ToastContentBuilder();
        foreach (var text in IngestToastFormatter.BuildTexts(payload))
            builder.AddText(text);

        builder.Show();
    }
}
