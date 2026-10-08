namespace Airshelf.Gui.Core.Updates;

/// <summary>
/// アプリ内自己更新(ADR-0013)の狭い仲介面(#157)。実装は <c>Airshelf.Gui</c> の <c>UpdateService</c>(Velopack)。
/// バージョン情報ページの ViewModel と <see cref="UpdateChecker"/> はこの interface だけを見るので、Velopack を
/// <c>Gui.Core</c> に持ち込まずに WSL でもテストできる。すべて UI スレッドから呼ぶ。
/// </summary>
public interface IAppUpdater
{
    /// <summary>Velopack でインストールされて動いているか(開発版・portable では false で、更新の項目を出さない)。</summary>
    bool IsInstalled { get; }

    /// <summary>保留中の(適用できる)更新の版。無ければ null。</summary>
    string? PendingVersion { get; }

    /// <summary>
    /// 更新を照会し、適用できる更新があれば保留にして true を返す。例外(ネットワーク・認証の失敗)は投げる
    /// (<see cref="UpdateChecker"/> が握る)。
    /// </summary>
    Task<bool> CheckAsync();

    /// <summary>
    /// 保留中の更新を取得し、取り込みを安全に止めてから適用して再起動する(成功すれば戻らない)。
    /// 保留が無ければ何もしない。失敗は例外で返す。
    /// </summary>
    Task ApplyAndRestartAsync();
}
