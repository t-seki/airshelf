namespace Radirec.Hosting.Control;

/// <summary>
/// 常駐の単一直列ループ(<c>RadioMonitorWorker.ExecuteAsync</c>)の生死と、停止要求とそれ以外の取り消しの区別(#197 / #204)。
/// Windows 専用の Radirec.Gui から切り出し、Linux でもテストできるようにする。
/// <para>
/// 停止要求は <c>stoppingToken</c> による <see cref="OperationCanceledException"/> だけ。呼んだ先の独自の時間切れなどで
/// 投げられる <see cref="OperationCanceledException"/>(<see cref="TaskCanceledException"/> を含む)は停止要求として扱わない
/// (<see cref="ScanRunQueue.RunAsync"/> と同じ区別)。
/// </para>
/// </summary>
public sealed class SerialLoop
{
    // 起動〜稼働中は true(ループ開始前の要求もチャネルにバッファされ処理される)。ループが抜けた後(停止・起動処理の失敗)は false。
    private volatile bool _running = true;

    /// <summary>ループが要求を処理できる状態か。状態取得の「稼働中」と、要求を受理するかの判定に使う。</summary>
    public bool Running => _running;
    private volatile Exception? _startFailure;
    /// <summary>
    /// 起動処理(<c>start</c>)が投げた例外。起動処理が終わった・まだ走っていないときは null。状態取得で、ループの異常終了と
    /// 起動処理の失敗を見分けて伝えるために使う(#217)。
    /// </summary>
    public Exception? StartFailure => _startFailure;

    /// <summary>
    /// 起動処理(<paramref name="start"/>)→ ループ(<paramref name="loop"/>)を走らせる。どこで抜けても(起動処理の例外を含む)
    /// <see cref="Running"/> を false にしてから <paramref name="onStopped"/> を呼ぶ。
    /// <para>
    /// 停止要求で抜けたときは正常終了。停止要求でないのにループが抜けた(停止要求でない取り消し・ループの自然終了)ときは
    /// <paramref name="onUnexpectedExit"/> に渡す(取り込みだけが黙って止まらないよう gui.log に残すため)。
    /// </para>
    /// <para>
    /// 起動処理の失敗(停止要求の取り消しを除く)は <see cref="StartFailure"/> に記録し、<paramref name="onStartFailed"/> に
    /// 渡してから投げ直す(#217)。.NET 10 から <c>BackgroundService.StartAsync</c> は <c>ExecuteAsync</c> の全体を別のタスクで
    /// 走らせるので、ここで投げ直した例外はホストの起動(<c>host.StartAsync()</c>)には伝わらない。呼び手が gui.log に残せるのは
    /// <paramref name="onStartFailed"/> の中だけ。
    /// </para>
    /// </summary>
    public async Task RunAsync(
        Action start,
        Func<CancellationToken, Task> loop,
        Action onStopped,
        Action<Exception?> onUnexpectedExit,
        Action<Exception> onStartFailed,
        CancellationToken stoppingToken)
    {
        try
        {
            try
            {
                start();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                _startFailure = ex;
                onStartFailed(ex);
                throw;
            }
            await loop(stoppingToken).ConfigureAwait(false);
            if (!stoppingToken.IsCancellationRequested)
                onUnexpectedExit(null);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停止要求。正常終了。
        }
        catch (OperationCanceledException ex) when (_startFailure is null)
        {
            onUnexpectedExit(ex);
        }
        finally
        {
            _running = false; // 以降の要求は受理しない(誤成功を返さない)。
            onStopped();
        }
    }

    /// <summary>
    /// ループの 1 つの処理(走査・再適用のジョブ)を走らせる。停止要求の取り消しだけは <paramref name="onStopRequested"/> を
    /// 呼んでから投げ直し、ループを終わらせる。それ以外の例外(停止要求でない取り消しを含む)は <paramref name="onFailure"/> に
    /// 渡して握り、ループは次のトリガへ進む(ここから例外を漏らすと常駐ごと落ちる)。
    /// </summary>
    public static async Task RunStepAsync(
        Func<CancellationToken, Task> step,
        Action<Exception> onFailure,
        CancellationToken stoppingToken,
        Action? onStopRequested = null)
    {
        try
        {
            await step(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            onStopRequested?.Invoke();
            throw; // 停止要求はループへ伝播させて終了する。
        }
        catch (Exception ex)
        {
            onFailure(ex);
        }
    }
}
