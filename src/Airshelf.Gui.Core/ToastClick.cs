namespace Airshelf.Gui.Core;

/// <summary>トレイが出すトーストの種類。</summary>
public enum IngestToastKind
{
    /// <summary>新規取込(「新規取込 N件」)。</summary>
    Summary,

    /// <summary>取込失敗(「取込失敗 N件」。ADR-0011)。</summary>
    Failure,

    /// <summary>保留(「判断待ち N件」。ADR-0021)。</summary>
    Held,
}

/// <summary>
/// トーストのクリック(#237)。保留と取込失敗のトーストは、クリックで管理画面のホーム(要対応)を開く。どちらも要対応に
/// 載る事柄の知らせなので、トーストからそのまま判断へ行けるようにする。新規取込のトーストは何もしない(取込履歴で足りる)。
/// トーストの起動引数(<c>ToastArguments</c> の 1 組)に <see cref="ArgumentKey"/> = <see cref="OpenHome"/> を載せ、
/// クリックで受け取った引数からそれを読む。
/// </summary>
public static class ToastClick
{
    /// <summary>起動引数のキー。</summary>
    public const string ArgumentKey = "action";

    /// <summary>管理画面のホームを開く。</summary>
    public const string OpenHome = "openHome";

    /// <summary>そのトーストに載せる <see cref="ArgumentKey"/> の値。クリックしても何もしないトーストは null(引数を載せない)。</summary>
    public static string? ActionFor(IngestToastKind kind) => kind switch
    {
        IngestToastKind.Failure or IngestToastKind.Held => OpenHome,
        _ => null,
    };

    /// <summary>クリックで受け取った <see cref="ArgumentKey"/> の値(無ければ null)が、管理画面のホームを開くものか。</summary>
    public static bool OpensHome(string? action) => string.Equals(action, OpenHome, StringComparison.Ordinal);
}

/// <summary>
/// トーストのクリックで管理画面のホームを開く要求を、開く先(管理画面の窓口)ができるまで預かる(#237)。
/// クリックは起動の途中にも届く(トレイが動いていないときにアクションセンターのトーストをクリックすると、exe が起動し、
/// その起動の中でクリックが届く)。開く先がまだ無ければ預かり、<see cref="Attach"/> で渡されたときに 1 度だけ開く。
/// 預かっている間に何度クリックされても開くのは 1 度(管理画面は同時に 1 つ。#151)。<see cref="Request"/> はどのスレッドから
/// 呼んでもよく、開く処理(<see cref="Attach"/> に渡したもの)は呼んだスレッドでそのまま呼ぶ(UI スレッドへ回すのは渡す側)。
/// </summary>
public sealed class PendingOpenRequests
{
    private readonly object _gate = new();
    private Action? _open;
    private bool _pending;

    /// <summary>開く。開く先がまだ無ければ預かる。</summary>
    public void Request()
    {
        Action open;
        lock (_gate)
        {
            if (_open is null)
            {
                _pending = true;
                return;
            }
            open = _open;
        }
        open();
    }

    /// <summary>開く先を渡す。預かっている要求があれば、ここで 1 度だけ開く。</summary>
    public void Attach(Action open)
    {
        bool run;
        lock (_gate)
        {
            _open = open;
            run = _pending;
            _pending = false;
        }
        if (run)
            open();
    }
}
