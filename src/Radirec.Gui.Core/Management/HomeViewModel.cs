using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Radirec.Hosting.Control;

namespace Radirec.Gui.Core.Management;

/// <summary>
/// 管理画面のホーム(#151)。「状態と再スキャン」と「取込履歴(新しい方の <see cref="RecentCount"/> 件)」だけを持つ
/// (要対応の欄は #155 / #156 が足す。調べていないのに「要対応はありません」と出さないため)。
/// <para>
/// 状態は、管理画面が開いている間だけシェルのタイマーが <see cref="Refresh"/> を呼んで読み直す(2 秒ごと)。
/// 数件の取込履歴は、最終スキャンの完了時刻(<see cref="ServiceStatusSnapshot.LastScanCompletedUtc"/>)が
/// 変わったときだけ台帳を読み直す(取り込みが増えうるのはスキャンの後だけなので)。
/// </para>
/// </summary>
public sealed class HomeViewModel : ObservableObject
{
    /// <summary>ホームに出す取込履歴の件数(新しい方から)。</summary>
    public const int RecentCount = 5;

    /// <summary>W-111。再スキャンを押した後、次のスキャンが終わるまでボタンの横に出す(バルーンは出さない)。</summary>
    public const string RescanRequested = "手動再スキャンを要求しました";

    /// <summary>W-112。取り込みが止まっている(W-103〜W-105)ときのボタン横の説明。</summary>
    public const string RescanUnavailable = "取り込みが止まっているため、再スキャンできません";

    /// <summary>W-113。再スキャンの要求が例外で失敗したとき(ボタンの横に出す)。</summary>
    public const string RescanFailed = "再スキャンを要求できませんでした(gui.log 参照)";

    private readonly IServiceControl? _control;
    private readonly string? _degradedReason;
    private readonly Func<IReadOnlyList<IngestHistoryRow>>? _loadHistory;
    private readonly Action<string> _logWarn;
    private readonly TimeZoneInfo? _zone;

    private string _statusText = "";
    private string _lastScanText = "";
    private bool _canRescan;
    private string? _rescanNote;
    private bool _rescanPending;              // W-111 を出している(次のスキャンの完了を待っている)
    private DateTimeOffset? _rescanBaseline;  // 再スキャンを押したときの最終スキャンの完了時刻
    private bool _rescanFailed;
    private DateTimeOffset? _historyScan;     // 数件の取込履歴を読んだときの最終スキャンの完了時刻
    private IReadOnlyList<IngestHistoryRow> _recentRows = [];
    private HistoryLoadState _historyState;

    /// <param name="control">取り込みワーカーの制御面。縮退(ワーカーが無い)なら null。</param>
    /// <param name="degradedReason">control が null の理由(W-104 / W-105 の括弧の中)。</param>
    /// <param name="loadHistory">台帳の全件を新しい順で返す。設定を読めず台帳の場所が決まらないなら null(W-150)。</param>
    /// <param name="logWarn">gui.log への記録。</param>
    /// <param name="showAll">「すべて表示」(W-142)。取込履歴ページへ移る。</param>
    /// <param name="zone">時刻を表示する時間帯(既定はローカル。テスト用)。</param>
    public HomeViewModel(
        IServiceControl? control,
        string? degradedReason,
        Func<IReadOnlyList<IngestHistoryRow>>? loadHistory,
        Action<string> logWarn,
        Action showAll,
        TimeZoneInfo? zone = null)
    {
        _control = control;
        _degradedReason = degradedReason;
        _loadHistory = loadHistory;
        _logWarn = logWarn;
        _zone = zone;

        RescanCommand = new RelayCommand(Rescan, () => CanRescan);
        ShowAllCommand = new RelayCommand(showAll);

        var status = ReadStatus();
        Apply(status);
        LoadRecent(status?.LastScanCompletedUtc);
    }

    /// <summary>W-101〜W-105。</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>W-106〜W-108。</summary>
    public string LastScanText { get => _lastScanText; private set => SetProperty(ref _lastScanText, value); }

    /// <summary>「今すぐ再スキャン」(W-110)を押せるか。取り込みが止まっていれば押せない。</summary>
    public bool CanRescan
    {
        get => _canRescan;
        private set
        {
            if (SetProperty(ref _canRescan, value))
                RescanCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>ボタンの横の 1 行(W-111 / W-112 / 要求の失敗)。何も出さないときは null。</summary>
    public string? RescanNote { get => _rescanNote; private set => SetProperty(ref _rescanNote, value); }

    /// <summary>新しい方の数件(最大 <see cref="RecentCount"/> 件)。</summary>
    public IReadOnlyList<IngestHistoryRow> RecentRows { get => _recentRows; private set => SetProperty(ref _recentRows, value); }

    /// <summary>数件の取込履歴を出せているか。</summary>
    public HistoryLoadState HistoryState
    {
        get => _historyState;
        private set
        {
            if (SetProperty(ref _historyState, value))
            {
                OnPropertyChanged(nameof(IsHistoryLoaded));
                OnPropertyChanged(nameof(IsHistoryFailed));
                OnPropertyChanged(nameof(IsHistoryUnavailable));
            }
        }
    }

    public bool IsHistoryLoaded => HistoryState == HistoryLoadState.Loaded;
    public bool IsHistoryFailed => HistoryState == HistoryLoadState.Failed;
    public bool IsHistoryUnavailable => HistoryState == HistoryLoadState.Unavailable;

    /// <summary>W-140 の <c>新しい方の {N} 件</c>。台帳を読めたときだけ。</summary>
    public string? RecentCaption => IsHistoryLoaded ? HistoryText.Recent(RecentRows.Count) : null;

    /// <summary>「今すぐ再スキャン」(W-110)。</summary>
    public RelayCommand RescanCommand { get; }

    /// <summary>「すべて表示」(W-142)。</summary>
    public RelayCommand ShowAllCommand { get; }

    /// <summary>
    /// 状態を読み直す(管理画面が開いている間、シェルのタイマーが 2 秒ごとに呼ぶ)。最終スキャンが変わっていれば
    /// 数件の取込履歴も読み直し、W-111 を下ろす。例外は gui.log に書いて握る(タイマーから UI スレッドへ漏らさない)。
    /// </summary>
    public void Refresh()
    {
        var status = ReadStatus();
        // ワーカーはあるのに状態を読めなかった(GetStatus が例外。ログは書いた)ときは、表示を変えずに次の回を待つ。
        if (_control is not null && status is null)
            return;
        Apply(status);

        var completed = status?.LastScanCompletedUtc;
        if (completed != _historyScan)
            LoadRecent(completed);
    }

    private ServiceStatusSnapshot? ReadStatus()
    {
        if (_control is null)
            return null;
        try
        {
            return _control.GetStatus();
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 状態を読めませんでした: {ex}");
            return null;
        }
    }

    private void Apply(ServiceStatusSnapshot? status)
    {
        // 状態を読めない(ワーカーが無い・GetStatus が例外)ときは、取り込みが止まっている扱いにする。
        var view = status is null
            ? ServiceStatusText.Stopped(_control is null ? _degradedReason : null)
            : ServiceStatusText.From(status, _zone);
        StatusText = view.Status;
        LastScanText = view.LastScan;
        CanRescan = view.CanControl;

        // W-111 は、押したときから最終スキャンの完了時刻が変わる(= 次のスキャンが終わる)まで出す。
        if (_rescanPending && (!view.CanControl || status?.LastScanCompletedUtc != _rescanBaseline))
            _rescanPending = false;
        if (!view.CanControl)
            _rescanFailed = false;

        RescanNote = !view.CanControl ? RescanUnavailable
            : _rescanFailed ? RescanFailed
            : _rescanPending ? RescanRequested
            : null;
    }

    private void Rescan()
    {
        if (_control is null || !CanRescan)
            return;

        try
        {
            // 要求チャネルへ coalesce される冪等操作。すぐ返る(スキャンの完了は待たない)。
            var baseline = _control.GetStatus().LastScanCompletedUtc; // 押した時点の最終スキャン
            _control.RequestRescan();
            _rescanBaseline = baseline;
            _rescanPending = true;
            _rescanFailed = false;
            RescanNote = RescanRequested;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 再スキャンを要求できませんでした: {ex}");
            _rescanPending = false;
            _rescanFailed = true;
            RescanNote = RescanFailed;
        }
    }

    private void LoadRecent(DateTimeOffset? scanCompleted)
    {
        _historyScan = scanCompleted;

        if (_loadHistory is null)
        {
            RecentRows = [];
            HistoryState = HistoryLoadState.Unavailable;
            OnPropertyChanged(nameof(RecentCaption));
            return;
        }

        try
        {
            RecentRows = _loadHistory().Take(RecentCount).ToList();
            HistoryState = HistoryLoadState.Loaded;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 取込履歴を読めませんでした(ホーム): {ex}");
            RecentRows = [];
            HistoryState = HistoryLoadState.Failed;
        }
        OnPropertyChanged(nameof(RecentCaption));
    }
}
