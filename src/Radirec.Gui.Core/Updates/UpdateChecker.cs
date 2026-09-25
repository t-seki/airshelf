namespace Radirec.Gui.Core.Updates;

/// <summary>更新の確認の結果(<see cref="UpdateChecker.CheckAsync"/>)。</summary>
public enum UpdateCheckOutcome
{
    /// <summary>別の確認(自動と手動)が走っている最中だった(W-505)。照会はしていない。</summary>
    Busy,

    /// <summary>最新版だった(W-503)。</summary>
    UpToDate,

    /// <summary>適用できる更新があった(W-504)。</summary>
    Found,

    /// <summary>照会に失敗した(W-506)。理由は gui.log。</summary>
    Failed,
}

/// <param name="Outcome">結果。</param>
/// <param name="Version">見つけた更新の版(<see cref="UpdateCheckOutcome.Found"/> のときだけ)。</param>
/// <param name="NotifyBalloon">
/// 自動の確認で、この更新を初めて見つけた(バルーン W-609 で知らせる)。手動の確認では常に false(結果は画面の中に出す)。
/// </param>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, string? Version = null, bool NotifyBalloon = false);

/// <summary>
/// 更新の確認(#157)。トレイの自動の確認(起動 10 秒後 + 24h ごと。<c>UpdateCoordinator</c>)と、バージョン情報ページの
/// 「今すぐ更新を確認」が同じインスタンスを使う。
/// <list type="bullet">
/// <item>2 つが重なると同じ UpdateManager へ二重に照会するので、確認の途中に来たものは照会せず <see cref="UpdateCheckOutcome.Busy"/> を返す</item>
/// <item>自動の確認で更新を見つけたときは、同じ更新については 1 度だけバルーンを出す(<see cref="UpdateCheckResult.NotifyBalloon"/>)。
/// 手動で見つけた更新も「知らせた」扱いにする(画面で見ているので、後の自動の確認でバルーンを重ねない)</item>
/// <item>更新が無い状態に戻ったら(適用した・取り下げられた)、次の新しい版でもう一度知らせる</item>
/// </list>
/// すべて UI スレッドから呼ぶ(状態は bool で足りる)。
/// </summary>
public sealed class UpdateChecker
{
    private readonly IAppUpdater _updater;
    private readonly Action<string> _logWarn;
    private bool _checking;
    private bool _notified;

    /// <param name="updater">更新の仕組み。</param>
    /// <param name="logWarn">gui.log への記録(照会の失敗)。</param>
    public UpdateChecker(IAppUpdater updater, Action<string> logWarn)
    {
        _updater = updater;
        _logWarn = logWarn;
    }

    /// <summary>更新を照会する。<paramref name="manual"/> はバージョン情報ページの「今すぐ更新を確認」。</summary>
    public async Task<UpdateCheckResult> CheckAsync(bool manual)
    {
        if (_checking)
            return new UpdateCheckResult(UpdateCheckOutcome.Busy);

        _checking = true;
        try
        {
            if (!await _updater.CheckAsync())
            {
                _notified = false; // 更新が無い状態に戻った。次の新しい版で、もう一度知らせる
                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
            }

            var notify = !manual && !_notified;
            _notified = true;
            return new UpdateCheckResult(UpdateCheckOutcome.Found, _updater.PendingVersion, notify);
        }
        catch (Exception ex)
        {
            _logWarn($"更新チェックに失敗しました: {ex.Message}");
            return new UpdateCheckResult(UpdateCheckOutcome.Failed);
        }
        finally
        {
            _checking = false;
        }
    }
}
