namespace Airshelf.Gui.Core.Updates;

/// <summary>
/// 更新まわりの文言(#157)。ID は docs/mockups/management-window.md の「バージョン情報」と「トレイのメニュー(新)」。
/// バージョン情報ページ・トレイ・自動の確認のバルーンで同じものを使う。
/// </summary>
public static class UpdateTexts
{
    /// <summary>W-501 / W-601。</summary>
    public static string VersionLine(string version) => $"Radirec {version}";

    /// <summary>W-502(ボタン)。</summary>
    public const string CheckButton = "今すぐ更新を確認";

    /// <summary>W-503。</summary>
    public const string UpToDate = "最新版です。";

    /// <summary>W-504。</summary>
    public static string Found(string? version) => $"更新があります(v{version})。";

    /// <summary>W-505。自動の確認と重なった。</summary>
    public const string Busy = "更新を確認中です。少し待って再度お試しください。";

    /// <summary>W-506。</summary>
    public const string CheckFailed = "更新チェックに失敗しました(gui.log 参照)。";

    /// <summary>W-507 / W-605(ボタン・トレイの項目)。</summary>
    public static string ApplyButton(string version) => $"更新 v{version} を適用して再起動";

    /// <summary>W-508(適用の確認)。</summary>
    public static string ApplyConfirm(string version) =>
        $"更新 v{version} を適用してアプリを再起動します。取り込み中の走査は完了を待ってから安全に再起動します。よろしいですか?";

    /// <summary>W-508 の確認のダイアログの表題。</summary>
    public const string ApplyConfirmCaption = "更新の適用";

    /// <summary>W-509(1 つ目)。押した時点で保留が消えていた。</summary>
    public const string NothingToApply = "適用できる更新はありません(最新か、取り下げられました)。";

    /// <summary>W-509(2 つ目)。</summary>
    public const string ApplyFailed = "更新の適用に失敗しました(gui.log 参照)。";

    /// <summary>W-510。Velopack で入れていない版(開発版・portable)。</summary>
    public const string NotUpdatable = "この版は自己更新の対象外です(開発版・portable)";

    /// <summary>W-511。Velopack で入れた版だが、設定を読めないので更新の仕組みを作れない(W-137 / W-150 と同じ言い方)。</summary>
    public const string ConfigUnreadable = "設定を読めないため、更新を確認できません";

    /// <summary>W-609。自動の確認で初めて更新を見つけたときのバルーン。</summary>
    public static string FoundBalloon(string? version) =>
        $"更新があります(v{version})。Radirec を開いて「バージョン情報」か、トレイメニューから適用できます。";
}
