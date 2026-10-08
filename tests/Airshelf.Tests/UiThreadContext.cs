using System.Collections.Concurrent;

namespace Airshelf.Tests;

/// <summary>
/// UI スレッドの代わり(テスト用)。1 本のスレッドで、await の続きを積まれた順に 1 つずつ動かす。
/// WPF と同じく、テストの本体が await で手放すまで続きは動かないので、「await の間に利用者が操作した」を決まった順で再現できる。
/// </summary>
internal sealed class UiThreadContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    // 本体が終わった後に届いた続き(待たずに走らせたもの)は捨てる。
    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    /// <summary><paramref name="body"/> をこの文脈で動かし、終わるまで待つ(例外はそのまま投げる)。</summary>
    public static void Run(Func<Task> body)
    {
        var previous = Current;
        var context = new UiThreadContext();
        SetSynchronizationContext(context);
        try
        {
            var task = body();
            task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
                callback(state);
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}
