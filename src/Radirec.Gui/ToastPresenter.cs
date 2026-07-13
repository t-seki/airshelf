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
        var (title, lines) = IngestToastFormatter.Format(payload);

        // WinRT トーストの <text> は埋め込み改行を折り返さないため、行ごとに AddText する。
        // ただし text 要素が上限(表題含め 4)に達すると ToastContentBuilder が例外を投げるため、
        // 表題 + 本文 MaxLines(=計 1+MaxLines)を硬い上限として切り詰める(formatter 側と二重の安全網)。
        var texts = new List<string> { title };
        texts.AddRange(lines);

        var builder = new ToastContentBuilder();
        foreach (var text in texts.Take(1 + IngestToastFormatter.MaxLines))
            builder.AddText(text);

        builder.Show();
    }
}
