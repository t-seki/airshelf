using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Radirec.Hosting.Control;

namespace Radirec.Gui.Core.Management;

/// <summary>
/// 管理画面のホーム(#151)。「状態と再スキャン」「要対応」「取込履歴(新しい方の <see cref="RecentCount"/> 件)」を持つ。
/// 要対応は、いまは「統合設定の無い兄弟フォルダ」だけ(#155。解決していない取込失敗と、設定と食い違った回は #156 が足す)。
/// <para>
/// 状態は、管理画面が開いている間だけシェルのタイマーが <see cref="Refresh"/> を呼んで読み直す(2 秒ごと)。
/// 数件の取込履歴と要対応は、最終スキャンの完了時刻(<see cref="ServiceStatusSnapshot.LastScanCompletedUtc"/>)が
/// 変わったときだけ読み直す(取り込みが増えうるのはスキャンの後だけなので)。要対応は、ホームへ切り替えたとき
/// (<see cref="RefreshAttention"/>)と「分けておく」を書いた後にも計算し直す(ADR-0018。2 秒ごとには計算しない)。
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
    private readonly ProgramsApplyGate? _applyGate;

    private string _statusText = "";
    private bool _canControl;                 // 取り込みが動いている(制御できる)
    private string _lastScanText = "";
    private bool _canRescan;
    private string? _rescanNote;
    private bool _rescanPending;              // W-111 を出している(次のスキャンの完了を待っている)
    private DateTimeOffset? _rescanBaseline;  // 再スキャンを押したときの最終スキャンの完了時刻
    private bool _rescanFailed;
    private DateTimeOffset? _historyScan;     // 数件の取込履歴を読んだときの最終スキャンの完了時刻
    private IReadOnlyList<IngestHistoryRow> _recentRows = [];
    private HistoryLoadState _historyState;

    private readonly IAttentionActions? _attention;
    private HistoryLoadState? _attentionState;
    private bool _attentionRunning;           // 要対応を計算している(UI スレッドの外で)
    private bool _attentionAgain;             // 計算の途中に計算し直しを頼まれた
    private string? _attentionError;
    private string? _attentionNote;
    private string? _keepApartBlocked;
    private IReadOnlyList<SiblingAttentionRow> _siblingRows = [];

    /// <param name="control">取り込みワーカーの制御面。縮退(ワーカーが無い)なら null。</param>
    /// <param name="degradedReason">control が null の理由(W-104 / W-105 の括弧の中)。</param>
    /// <param name="loadHistory">台帳の全件を新しい順で返す。設定を読めず台帳の場所が決まらないなら null(W-150)。</param>
    /// <param name="logWarn">gui.log への記録。</param>
    /// <param name="showAll">「すべて表示」(W-142)。取込履歴ページへ移る。</param>
    /// <param name="zone">時刻を表示する時間帯(既定はローカル。テスト用)。</param>
    /// <param name="applyGate">番組の保存と適用の途中か(#154)。途中は再スキャンを押せない(W-110)。</param>
    /// <param name="attention">要対応の計算と「統合する」「分けておく」。設定を読めない(縮退)なら null(W-137)。</param>
    public HomeViewModel(
        IServiceControl? control,
        string? degradedReason,
        Func<IReadOnlyList<IngestHistoryRow>>? loadHistory,
        Action<string> logWarn,
        Action showAll,
        TimeZoneInfo? zone = null,
        ProgramsApplyGate? applyGate = null,
        IAttentionActions? attention = null)
    {
        _control = control;
        _degradedReason = degradedReason;
        _loadHistory = loadHistory;
        _logWarn = logWarn;
        _zone = zone;
        _applyGate = applyGate;
        _attention = attention;

        RescanCommand = new RelayCommand(Rescan, () => CanRescan);
        ShowAllCommand = new RelayCommand(showAll);
        if (_applyGate is not null)
            _applyGate.Changed += (_, _) => UpdateCanRescan();

        var status = ReadStatus();
        Apply(status);
        LoadRecent(status?.LastScanCompletedUtc);
        RefreshAttention();
    }

    /// <summary>W-101〜W-105。</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>W-106〜W-108。</summary>
    public string LastScanText { get => _lastScanText; private set => SetProperty(ref _lastScanText, value); }

    /// <summary>
    /// 「今すぐ再スキャン」(W-110)を押せるか。取り込みが止まっていれば押せない。番組の保存と適用の途中も押せない(#154)。
    /// </summary>
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
        {
            LoadRecent(completed);
            RefreshAttention();
        }
        else
        {
            UpdateKeepApartAvailability();
        }
    }

    // ---- 要対応(ADR-0018・#155)----

    /// <summary>
    /// 要対応を出せているか(台帳と設定を読めた / 読めなかった / 縮退で出せない)。まだ計算し終えていなければ null
    /// (調べていないのに「要対応はありません」と出さないため)。
    /// </summary>
    public HistoryLoadState? AttentionState
    {
        get => _attentionState;
        private set
        {
            if (SetProperty(ref _attentionState, value))
                OnPropertyChanged(nameof(IsAttentionLoaded));
        }
    }

    public bool IsAttentionLoaded => AttentionState == HistoryLoadState.Loaded;

    /// <summary>W-137(縮退・読めなかった)。出せていれば null。</summary>
    public string? AttentionError
    {
        get => _attentionError;
        private set
        {
            if (SetProperty(ref _attentionError, value))
                OnPropertyChanged(nameof(IsAttentionErrorVisible));
        }
    }

    public bool IsAttentionErrorVisible => AttentionError is not null;

    /// <summary>W-122 の組(W-123〜W-127)。</summary>
    public IReadOnlyList<SiblingAttentionRow> SiblingRows
    {
        get => _siblingRows;
        private set
        {
            if (!SetProperty(ref _siblingRows, value))
                return;
            OnPropertyChanged(nameof(HasSiblings));
            OnPropertyChanged(nameof(AttentionCount));
            OnPropertyChanged(nameof(AttentionCountText));
            OnPropertyChanged(nameof(IsAttentionEmpty));
        }
    }

    public bool HasSiblings => SiblingRows.Count > 0;

    /// <summary>要対応の件数(種類をまたいだ合計)。</summary>
    public int AttentionCount => SiblingRows.Count;

    /// <summary>W-120 の <c>要対応 {N} 件</c>。1 件以上のときだけ。</summary>
    public string? AttentionCountText => IsAttentionLoaded && AttentionCount > 0 ? AttentionText.Count(AttentionCount) : null;

    /// <summary>W-121 を出すか(計算できて、どの種類も無い)。</summary>
    public bool IsAttentionEmpty => IsAttentionLoaded && AttentionCount == 0;

    /// <summary>「分けておく」で書けなかった理由(W-139)。無ければ null。</summary>
    public string? AttentionNote
    {
        get => _attentionNote;
        private set
        {
            if (SetProperty(ref _attentionNote, value))
                OnPropertyChanged(nameof(IsAttentionNoteVisible));
        }
    }

    public bool IsAttentionNoteVisible => AttentionNote is not null;

    /// <summary>「分けておく」を押せない理由(W-138)。兄弟フォルダの組があって、押せないときだけ。</summary>
    public string? KeepApartBlocked
    {
        get => _keepApartBlocked;
        private set
        {
            if (SetProperty(ref _keepApartBlocked, value))
                OnPropertyChanged(nameof(IsKeepApartBlockedVisible));
        }
    }

    public bool IsKeepApartBlockedVisible => KeepApartBlocked is not null;

    /// <summary>いま走っている(最後に始めた)要対応の計算。終わっていれば完了済みのタスク(テストで待つ)。</summary>
    public Task AttentionRefreshed { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// 要対応を計算し直す(開いたとき・ホームへ切り替えたとき・最終スキャンが変わったとき・「分けておく」を書いた後)。
    /// 待たずに返る(<see cref="RefreshAttentionAsync"/>)。
    /// </summary>
    public void RefreshAttention() => _ = RefreshAttentionAsync();

    /// <summary>
    /// 要対応を計算し直す。計算(台帳と programs.json / stations.json を読む。ProgramsFile は NAS を指しうる)は UI スレッドの
    /// 外で行い、結果は呼んだスレッド(UI スレッド)で反映する。計算の途中に呼ばれたら、新しく並べて走らせず、今の計算が
    /// 終わってからもう 1 度だけ計算し直す(その間に変わったものを取りこぼさないため)。例外は gui.log に書いて握り、
    /// W-137 の 2 つ目を出す。
    /// </summary>
    public Task RefreshAttentionAsync()
    {
        if (_attention is null)
        {
            SiblingRows = [];
            AttentionState = HistoryLoadState.Unavailable;
            AttentionError = AttentionText.Unavailable; // W-137
            OnPropertyChanged(nameof(AttentionCountText));
            OnPropertyChanged(nameof(IsAttentionEmpty));
            return Task.CompletedTask;
        }

        if (_attentionRunning)
        {
            _attentionAgain = true;
            return AttentionRefreshed;
        }

        _attentionRunning = true;
        return AttentionRefreshed = ComputeAttentionAsync(_attention);
    }

    private async Task ComputeAttentionAsync(IAttentionActions attention)
    {
        try
        {
            do
            {
                _attentionAgain = false;
                try
                {
                    var items = await Task.Run(attention.Compute);
                    AttentionState = HistoryLoadState.Loaded;
                    AttentionError = null;
                    SiblingRows = items.UnintegratedSiblings
                        .Select(s => new SiblingAttentionRow(s, Integrate, KeepApartAsync))
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logWarn($"管理画面: 要対応を計算できませんでした: {ex}");
                    SiblingRows = [];
                    AttentionState = HistoryLoadState.Failed;
                    AttentionError = AttentionText.ComputeFailed(ex.Message);
                }
                OnPropertyChanged(nameof(AttentionCountText));
                OnPropertyChanged(nameof(IsAttentionEmpty));
                UpdateKeepApartAvailability();
            }
            while (_attentionAgain);
        }
        finally
        {
            _attentionRunning = false;
        }
    }

    // 「分けておく」を押せるか(番組ページに保存していない変更があれば押せない。W-138)。タイマーの 2 秒ごとにも読み直す。
    private void UpdateKeepApartAvailability()
    {
        string? blocked = null;
        try
        {
            blocked = _attention?.KeepApartBlockedReason;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 番組ページの状態を読めませんでした(要対応): {ex}");
        }
        foreach (var row in SiblingRows)
            row.CanKeepApart = blocked is null;
        KeepApartBlocked = HasSiblings ? blocked : null;
    }

    private void Integrate(SiblingAttentionRow row)
    {
        AttentionNote = null;
        try
        {
            _attention?.Integrate(row.Sibling.Group);
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 統合の下書きを作れませんでした: {ex}");
        }
    }

    private async Task KeepApartAsync(SiblingAttentionRow row)
    {
        if (_attention is null)
            return;
        AttentionNote = null;
        try
        {
            AttentionNote = await _attention.KeepApartAsync(row.Sibling.Group); // W-138 / W-139
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 分けておく組を書けませんでした: {ex}");
            AttentionNote = ProgramsPageViewModel.KeepApartFailed(ex.Message);
        }
        await RefreshAttentionAsync();
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
        _canControl = view.CanControl;
        UpdateCanRescan();

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

    private void UpdateCanRescan() => CanRescan = _canControl && _applyGate?.IsBusy != true;

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
