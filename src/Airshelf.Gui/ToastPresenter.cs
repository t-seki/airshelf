using System.Runtime.Versioning;
using CommunityToolkit.WinUI.Notifications;
using Airshelf.Gui.Core;

namespace Airshelf.Gui;

/// <summary>
/// 取込要約をネイティブトーストで表示する(ADR-0009)。<see cref="ToastNotificationManagerCompat"/> は
/// 非パッケージ Win32 でも動き、初回 Show 時に AUMID + スタートメニューショートカットを自動生成する。
/// 保留と取込失敗のトーストには、クリックで管理画面のホームを開く起動引数を載せる(<see cref="ToastClick"/>。#237)。
/// クリックの受け取りは Program(<c>ToastNotificationManagerCompat.OnActivated</c>)。実機で検証すること。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ToastPresenter
{
    /// <summary>取込通知(「新規取込 N件」)を表示する。起動時 catch-up の集約もこれで出す(ADR-0012)。</summary>
    public void Show(IngestSummaryPayload payload) =>
        Show(IngestToastFormatter.BuildTexts(payload), IngestToastKind.Summary);

    /// <summary>取込失敗通知(「取込失敗 N件」)を表示する(ADR-0011)。</summary>
    public void ShowFailure(IngestFailurePayload payload) =>
        Show(IngestToastFormatter.BuildFailureTexts(payload), IngestToastKind.Failure);

    /// <summary>保留の通知(「判断待ち N件」)を表示する(ADR-0021)。</summary>
    public void ShowHeld(IngestHeldPayload payload) =>
        Show(IngestToastFormatter.BuildHeldTexts(payload), IngestToastKind.Held);

    private static void Show(IReadOnlyList<string> texts, IngestToastKind kind)
    {
        // 要素数上限への切り詰めは formatter(テスト可能な純粋関数)に集約済み。ここは
        // 各要素を <text> として AddText するだけ(WinRT の <text> は埋め込み改行を折り返さない)。
        var builder = new ToastContentBuilder();
        foreach (var text in texts)
            builder.AddText(text);

        // 引数を載せないトースト(新規取込)は、クリックしても何もしない(トレイが動いていなければ、トレイが起動するだけ)。
        if (ToastClick.ActionFor(kind) is { } action)
            builder.AddArgument(ToastClick.ArgumentKey, action);

        builder.Show();
    }
}
