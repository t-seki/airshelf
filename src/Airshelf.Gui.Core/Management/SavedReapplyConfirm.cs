namespace Airshelf.Gui.Core.Management;

/// <summary>
/// 保存済みの設定での再適用の確認(<see cref="ProgramsPageViewModel.ConfirmSavedAsync"/>。#164)を始めた結果。
/// </summary>
/// <param name="ShowsConfirm">揃える回があり、番組ページに確認の画面(W-252 / W-253 / W-312)を出した。呼び出し元は番組ページへ移る。</param>
/// <param name="Message">番組ページへ移らないとき、来たページに出す 1 行(W-254 / W-255 / W-256)。移るときは null。</param>
/// <param name="IsWarning"><see cref="Message"/> が失敗・中止か(警告の見た目で出す)。</param>
public sealed record SavedReapplyStart(bool ShowsConfirm, string? Message, bool IsWarning)
{
    /// <summary>確認の画面を出した(番組ページへ移る)。</summary>
    public static SavedReapplyStart Confirm { get; } = new(ShowsConfirm: true, Message: null, IsWarning: false);

    /// <summary>番組ページへ移らず、来たページに <paramref name="message"/> を出す。</summary>
    public static SavedReapplyStart Stay(string message, bool isWarning) => new(ShowsConfirm: false, message, isWarning);
}

/// <summary>
/// 保存済みの設定で再適用を確かめる入口(#164)。設定ページの stations.json の「再読み込み」と、ホームの要対応の
/// 「再適用の確認へ」(W-136。#156)が使う。管理画面(<see cref="ManagementViewModel.SavedReapplyEntry"/>)が、来たページごとに作る。
/// </summary>
public interface ISavedReapplyEntry
{
    /// <summary>
    /// いま始められない理由(W-256)。始められるなら null。入口のボタンは押せなくし、理由は入口ごとの場所に出す
    /// (設定ページは stations.json の段、ホームは「再適用の確認へ」〈W-136〉の下)。
    /// </summary>
    string? BlockedReason { get; }

    /// <summary>
    /// 確かめる。揃える回があれば番組ページの確認の画面へ移る(戻り値の <see cref="SavedReapplyStart.ShowsConfirm"/>)。
    /// 無ければ移らずに、来たページに出す 1 行を返す。例外は投げない。
    /// </summary>
    Task<SavedReapplyStart> StartAsync();
}
