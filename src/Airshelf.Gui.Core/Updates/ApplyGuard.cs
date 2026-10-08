namespace Airshelf.Gui.Core.Updates;

/// <summary>
/// 更新の適用の再入を防ぐ(#157)。適用の入口はトレイの「更新を適用して再起動」とバージョン情報ページの 2 つあり、
/// 片方の適用がダウンロードを待っている間にもう片方から押されると、ダウンロード・取り込みの停止・差し替えが二重に走りうる。
/// <c>UpdateService.ApplyAndRestartAsync</c> の 1 か所で使い、両方の入口を防ぐ。すべて UI スレッドから呼ぶ(状態は bool で足りる)。
/// </summary>
public sealed class ApplyGuard
{
    private bool _running;

    /// <summary>適用の途中か。</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// 適用の途中でなければ <paramref name="apply"/> を実行して true を返す。途中なら何もせず false を返す。
    /// 適用が失敗(例外)したら途中の印を外して例外をそのまま投げる(もう一度押せるように)。
    /// </summary>
    public async Task<bool> RunAsync(Func<Task> apply)
    {
        if (_running)
            return false;

        _running = true;
        try
        {
            await apply();
            return true;
        }
        finally
        {
            _running = false;
        }
    }
}
