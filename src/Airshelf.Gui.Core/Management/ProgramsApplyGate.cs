namespace Airshelf.Gui.Core.Management;

/// <summary>
/// 番組の保存と適用の途中か(#154)。番組ページが「確認」を押してから、確認・保存・再適用が終わって一覧か結果に
/// 戻るまで立てる。その間はホームの「今すぐ再スキャン」を押せない(W-110)。トレイの再適用が持っていた
/// 「再適用の途中は再スキャンを受けない」(#142)を、管理画面の中で引き継ぐ(#162 のレビューから)。
/// UI スレッドだけが読み書きする。
/// </summary>
public sealed class ProgramsApplyGate
{
    private bool _isBusy;

    /// <summary>保存と適用の途中か。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary><see cref="IsBusy"/> が変わった。</summary>
    public event EventHandler? Changed;
}
