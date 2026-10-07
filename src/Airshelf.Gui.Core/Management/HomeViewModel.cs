using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Airshelf.Core.Configuration;
using Airshelf.Hosting.Control;

namespace Airshelf.Gui.Core.Management;

/// <summary>
/// 管理画面のホーム(#151)。「状態と再スキャン」「要対応」「取込履歴(新しい方の <see cref="RecentCount"/> 件)」を持つ。
/// 要対応は 4 種:統合設定の無い兄弟フォルダ(#155)、解決していない取込失敗と、番組の設定と食い違ったままの回(#156)、差し替え放送の疑い(#233)。
/// 差し替え放送の疑いの判断は、番組ページの下書きに積む(W-159〜W-164。#235)。
/// <para>
/// 状態は、管理画面が開いている間だけシェルのタイマーが <see cref="Refresh"/> を呼んで読み直す(2 秒ごと)。
/// 数件の取込履歴と要対応は、最終スキャンの完了時刻(<see cref="ServiceStatusSnapshot.LastScanCompletedUtc"/>)が
/// 変わったときだけ読み直す(取り込みが増えうるのはスキャンの後だけなので)。要対応は、ホームへ切り替えたとき
/// (<see cref="RefreshAttention"/>)と「分けておく」を書いた後にも計算し直す(ADR-0018。2 秒ごとには計算しない)。
/// </para>
/// <para>
/// 数件の取込履歴は、要対応と同じく UI スレッドの外で読む(NAS に届かないと SMB の時間切れまで返らないため。#222)。
/// 最初の読み込みの間だけ W-154 を出し、読み直しの間は前の行と状態を出したままにする。
/// </para>
/// </summary>
public sealed class HomeViewModel : ObservableObject
{
    /// <summary>ホームに出す取込履歴の件数(新しい方から)。</summary>
    public const int RecentCount = 5;

    /// <summary>W-111。再スキャンを押した後、次のスキャンが終わるまでボタンの横に出す(バルーンは出さない)。</summary>
    public const string RescanRequested = "手動再スキャンを要求しました";

    /// <summary>W-112。取り込みが止まっている(W-103〜W-105・W-153)ときのボタン横の説明。</summary>
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
    private DateTimeOffset? _historyScan;     // 数件の取込履歴を読み始めたときの最終スキャンの完了時刻
    private DateTimeOffset? _attentionScan;   // 要対応を計算し直したときの最終スキャンの完了時刻
    private IReadOnlyList<IngestHistoryRow> _recentRows = [];
    private HistoryLoadState _historyState = HistoryLoadState.Loading;
    private bool _historyRunning;             // 数件の取込履歴を読んでいる(UI スレッドの外で)

    private readonly IAttentionActions? _attention;
    private HistoryLoadState? _attentionState;
    private bool _attentionRunning;           // 要対応を計算している(UI スレッドの外で)
    private bool _attentionAgain;             // 計算の途中に計算し直しを頼まれた
    private string? _attentionError;
    private string? _attentionNote;
    private string? _keepApartBlocked;
    private IReadOnlyList<SiblingAttentionRow> _siblingRows = [];
    private IReadOnlyList<FailureAttentionRow> _failureRows = [];
    private IReadOnlyList<DriftAttentionRow> _driftRows = [];
    private IReadOnlyList<SubstituteAttentionRow> _substituteRows = [];
    private string? _substituteNote;          // W-163 / W-164(判断を積めなかった)
    private string? _stagedSummary;           // W-161
    private readonly IHistoryPathActions? _pathActions;
    private string? _failureNote;
    private readonly ISavedReapplyEntry? _savedReapply;
    private bool _reapplyChecking;            // 「再適用の確認へ」で確かめている途中
    private string? _reapplyResult;           // W-254 / W-255(最後に押した結果)
    private bool _reapplyResultWarning;

    /// <param name="control">取り込みワーカーの制御面。縮退(ワーカーが無い)なら null。</param>
    /// <param name="degradedReason">control が null の理由(W-104 / W-105 の括弧の中)。</param>
    /// <param name="loadHistory">台帳の全件を新しい順で返す。設定を読めず台帳の場所が決まらないなら null(W-150)。</param>
    /// <param name="logWarn">gui.log への記録。</param>
    /// <param name="showAll">「すべて表示」(W-142)。取込履歴ページへ移る。</param>
    /// <param name="zone">時刻を表示する時間帯(既定はローカル。テスト用)。</param>
    /// <param name="applyGate">番組の保存と適用の途中か(#154)。途中は再スキャンを押せない(W-110)。</param>
    /// <param name="attention">要対応の計算と「統合する」「分けておく」。設定を読めない(縮退)なら null(W-137)。</param>
    /// <param name="pathActions">取込失敗の取込元をエクスプローラーで開く(W-132)。null なら押しても何もしない。</param>
    /// <param name="savedReapply">
    /// 「再適用の確認へ」(W-136)の入口。保存済みの設定で再適用を確かめる(#164 と共有)。null なら押せない。
    /// </param>
    public HomeViewModel(
        IServiceControl? control,
        string? degradedReason,
        Func<IReadOnlyList<IngestHistoryRow>>? loadHistory,
        Action<string> logWarn,
        Action showAll,
        TimeZoneInfo? zone = null,
        ProgramsApplyGate? applyGate = null,
        IAttentionActions? attention = null,
        IHistoryPathActions? pathActions = null,
        ISavedReapplyEntry? savedReapply = null)
    {
        _control = control;
        _degradedReason = degradedReason;
        _loadHistory = loadHistory;
        _logWarn = logWarn;
        _zone = zone;
        _applyGate = applyGate;
        _attention = attention;
        _pathActions = pathActions;
        _savedReapply = savedReapply;

        ReapplyCommand = new AsyncRelayCommand(ReapplyAsync, () => CanReapply);
        RescanCommand = new RelayCommand(Rescan, () => CanRescan);
        ShowAllCommand = new RelayCommand(showAll);
        ShowProgramsCommand = new RelayCommand(() => _attention?.ShowPrograms());
        if (_applyGate is not null)
            _applyGate.Changed += (_, _) => UpdateCanRescan();

        var status = ReadStatus();
        Apply(status);
        LoadRecent(status?.LastScanCompletedUtc);
        _attentionScan = status?.LastScanCompletedUtc;
        RefreshAttention();
    }

    /// <summary>W-101〜W-105・W-153。</summary>
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
                OnPropertyChanged(nameof(IsHistoryLoading));
            }
        }
    }

    public bool IsHistoryLoaded => HistoryState == HistoryLoadState.Loaded;
    public bool IsHistoryFailed => HistoryState == HistoryLoadState.Failed;
    public bool IsHistoryUnavailable => HistoryState == HistoryLoadState.Unavailable;

    /// <summary>最初の読み込みの途中(W-154)。</summary>
    public bool IsHistoryLoading => HistoryState == HistoryLoadState.Loading;

    /// <summary>いま走っている(最後に始めた)数件の取込履歴の読み込み。終わっていれば完了済みのタスク(テストで待つ)。</summary>
    public Task HistoryRefreshed { get; private set; } = Task.CompletedTask;

    /// <summary>W-140 の <c>新しい方の {N} 件</c>。台帳を読めたときだけ。</summary>
    public string? RecentCaption => IsHistoryLoaded ? HistoryText.Recent(RecentRows.Count) : null;

    /// <summary>「今すぐ再スキャン」(W-110)。</summary>
    public RelayCommand RescanCommand { get; }

    /// <summary>「すべて表示」(W-142)。</summary>
    public RelayCommand ShowAllCommand { get; }

    /// <summary>
    /// 状態を読み直す(管理画面が開いている間、シェルのタイマーが 2 秒ごとに呼ぶ)。最終スキャンが変わっていれば
    /// 数件の取込履歴も読み直し、W-111 を下ろす。例外は gui.log に書いて握る(タイマーから UI スレッドへ漏らさない)。
    /// 数件の取込履歴を読んでいる途中なら、その回の読み直しは捨てる(時間切れを待つ読み込みを重ねない。#222)。
    /// 覚えるのは読み始めた回の完了時刻だけなので、まだ読んでいない完了時刻は、読み終えた後の回で読み直す。
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
        if (completed != _attentionScan)
        {
            _attentionScan = completed;
            RefreshAttention();
        }
        else
        {
            UpdateKeepApartAvailability();
            UpdateStagedDecisions(); // 番組ページで判断の行を直した・消した・保存した等に追従する
        }
        RefreshSavedReapply();
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
            NotifyAttentionCount();
        }
    }

    public bool HasSiblings => SiblingRows.Count > 0;

    /// <summary>W-128 の各件(W-129〜W-132)。記録した時刻の新しい順(時刻の無い失敗は後ろ)。</summary>
    public IReadOnlyList<FailureAttentionRow> FailureRows
    {
        get => _failureRows;
        private set
        {
            if (!SetProperty(ref _failureRows, value))
                return;
            OnPropertyChanged(nameof(HasFailures));
            NotifyAttentionCount();
        }
    }

    public bool HasFailures => FailureRows.Count > 0;

    /// <summary>
    /// W-134 の各まとまり(W-135。出力先から消す予定の W-158 も含む。ADR-0021)。計算し直して中身が変わったら、「再適用の確認へ」を
    /// 前に押した結果(W-254 / W-255)を消す(押していない食い違いに、前の結果が出ないように)。中身が同じなら残す(スキャンが
    /// 終わっただけでは消さない)。
    /// </summary>
    public IReadOnlyList<DriftAttentionRow> DriftRows
    {
        get => _driftRows;
        private set
        {
            if (!_driftRows.Select(r => r.Key).SequenceEqual(value.Select(r => r.Key)))
                _reapplyResult = null;
            if (!SetProperty(ref _driftRows, value))
                return;
            OnPropertyChanged(nameof(HasDrift));
            NotifyAttentionCount();
            RefreshSavedReapply();
        }
    }

    public bool HasDrift => DriftRows.Count > 0;

    /// <summary>W-155 の各組(W-156。ADR-0021)。</summary>
    public IReadOnlyList<SubstituteAttentionRow> SubstituteRows
    {
        get => _substituteRows;
        private set
        {
            if (!SetProperty(ref _substituteRows, value))
                return;
            OnPropertyChanged(nameof(HasSubstitutes));
            NotifyAttentionCount();
        }
    }

    public bool HasSubstitutes => SubstituteRows.Count > 0;

    /// <summary>W-163 / W-164(判断を下書きに積めなかった理由)。無ければ null。</summary>
    public string? SubstituteNote
    {
        get => _substituteNote;
        private set
        {
            if (SetProperty(ref _substituteNote, value))
                OnPropertyChanged(nameof(IsSubstituteNoteVisible));
        }
    }

    public bool IsSubstituteNoteVisible => SubstituteNote is not null;

    /// <summary>W-161(番組ページに、保存していない判断がある)。無ければ null。</summary>
    public string? StagedSummary
    {
        get => _stagedSummary;
        private set
        {
            if (SetProperty(ref _stagedSummary, value))
                OnPropertyChanged(nameof(IsStagedSummaryVisible));
        }
    }

    public bool IsStagedSummaryVisible => StagedSummary is not null;

    /// <summary>「番組ページへ」(W-162)。</summary>
    public RelayCommand ShowProgramsCommand { get; }

    /// <summary>4 種のどれかがある(要対応の欄を出す)。</summary>
    public bool HasAnyAttention => HasSiblings || HasFailures || HasDrift || HasSubstitutes;

    /// <summary>
    /// 要対応の件数(種類をまたいだ合計。兄弟フォルダの組・失敗・食い違いのまとまり〈消す予定を含む〉・差し替え放送の疑いの組を
    /// 1 件ずつ数える。<see cref="AttentionItems.Count"/> と同じ)。
    /// </summary>
    public int AttentionCount => SiblingRows.Count + FailureRows.Count + DriftRows.Count + SubstituteRows.Count;

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

    /// <summary>W-132 を押した結果(W-146 / W-147)。押していなければ null。</summary>
    public string? FailureNote
    {
        get => _failureNote;
        private set
        {
            if (SetProperty(ref _failureNote, value))
                OnPropertyChanged(nameof(IsFailureNoteVisible));
        }
    }

    public bool IsFailureNoteVisible => FailureNote is not null;

    /// <summary>
    /// 「再適用の確認へ」(W-136)を押せるか。食い違った回があり、取り込みが動いていて(W-119)、番組ページに保存していない
    /// 作業が無く(W-256)、確かめている途中でないとき。
    /// </summary>
    public bool CanReapply =>
        HasDrift && _canControl && _savedReapply is not null && ReadBlockedReason() is null && !_reapplyChecking;

    /// <summary>
    /// W-136 の下の 1 行。確かめている途中(W-240)、押せない理由(W-119 / W-256)、押した結果(W-254 / W-255)の順に、
    /// 出すものがあれば 1 つ。食い違った回が無ければ null。
    /// </summary>
    public string? ReapplyNote
    {
        get
        {
            if (!HasDrift || _savedReapply is null)
                return null;
            if (_reapplyChecking)
                return ProgramsPageViewModel.Checking; // W-240
            if (!_canControl)
                return AttentionText.ReapplyUnavailable; // W-119
            return ReadBlockedReason() ?? _reapplyResult; // W-256 / W-254 / W-255
        }
    }

    public bool IsReapplyNoteVisible => ReapplyNote is not null;

    /// <summary><see cref="ReapplyNote"/> が失敗・中止(W-255)か(警告の見た目で出す)。</summary>
    public bool IsReapplyNoteWarning => _reapplyResultWarning && ReapplyNote is { } note && note == _reapplyResult;

    /// <summary>「再適用の確認へ」(W-136)。</summary>
    public IAsyncRelayCommand ReapplyCommand { get; }

    /// <summary>
    /// 「再適用の確認へ」を押せるかを計算し直す(番組ページで編集を始めた・やめた、取り込みが止まった等に追従する)。
    /// 管理画面のタイマーが 2 秒ごとに(<see cref="Refresh"/> から)呼ぶ。
    /// </summary>
    public void RefreshSavedReapply()
    {
        OnPropertyChanged(nameof(CanReapply));
        OnPropertyChanged(nameof(ReapplyNote));
        OnPropertyChanged(nameof(IsReapplyNoteVisible));
        OnPropertyChanged(nameof(IsReapplyNoteWarning));
        ReapplyCommand?.NotifyCanExecuteChanged();
    }

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
            FailureRows = [];
            DriftRows = [];
            SubstituteRows = [];
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
                    FailureRows = items.UnresolvedFailures
                        .Select(f => new FailureAttentionRow(f, _zone, RevealFailureAsync))
                        .ToList();
                    DriftRows = items.SettingsDrift.Select(d => new DriftAttentionRow(d))
                        .Concat(items.PendingRemovals.Select(r => new DriftAttentionRow(r)))
                        .ToList();
                    SubstituteRows = items.SubstituteSuspects.Select(s => new SubstituteAttentionRow(s, StageAsync)).ToList();
                }
                catch (Exception ex)
                {
                    _logWarn($"管理画面: 要対応を計算できませんでした: {ex}");
                    SiblingRows = [];
                    FailureRows = [];
                    DriftRows = [];
                    SubstituteRows = [];
                    AttentionState = HistoryLoadState.Failed;
                    AttentionError = AttentionText.ComputeFailed(ex.Message);
                }
                NotifyAttentionCount();
                UpdateKeepApartAvailability();
                UpdateStagedDecisions();
                RefreshSavedReapply();
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

    // 差し替え放送の疑いの各組に積んだ判断(W-160)と、番組ページの保存していない判断の数(W-161)を読み直す。
    // 番組ページの一覧を読むだけ(ファイルは読まない)なので、タイマーの 2 秒ごとにも呼ぶ。
    private void UpdateStagedDecisions()
    {
        if (_attention is null)
            return;
        try
        {
            foreach (var row in SubstituteRows)
                row.Staged = _attention.StagedDecisionLabel(row.Suspect) is { } label ? AttentionText.Staged(label) : null;
            var count = _attention.StagedDecisionCount;
            StagedSummary = count > 0 ? AttentionText.StagedSummary(count) : null;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 番組ページの状態を読めませんでした(差し替え放送の判断): {ex}");
        }
    }

    // W-159。判断を番組ページの下書きに積む(ホームに留まる)。積めなかったら W-163 / W-164 を出す。
    private async Task StageAsync(SubstituteAttentionRow row, FilenameProgramTreatment treat)
    {
        if (_attention is null)
            return;
        try
        {
            SubstituteNote = await _attention.StageDecisionAsync(row.Suspect, treat);
        }
        catch (Exception ex)
        {
            // コマンドの例外を UI スレッドの未捕捉例外にしない(そちらは管理画面ごと閉じる最後の網)。
            _logWarn($"管理画面: 差し替え放送の判断を下書きに積めませんでした: {ex}");
            SubstituteNote = $"{ProgramsPageViewModel.StageUnavailable}: {ex.Message}";
        }
        UpdateStagedDecisions();
        UpdateKeepApartAvailability(); // 積むと番組ページに保存していない変更ができる(W-138)
        RefreshSavedReapply();         // 同じく W-256
    }

    private void NotifyAttentionCount()
    {
        OnPropertyChanged(nameof(HasAnyAttention));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(AttentionCountText));
        OnPropertyChanged(nameof(IsAttentionEmpty));
    }

    // 入口の「押せない理由」(番組ページの状態を読む)。例外は gui.log に書いて握る(タイマーから UI スレッドへ漏らさない)。
    private string? ReadBlockedReason()
    {
        try
        {
            return _savedReapply?.BlockedReason;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 番組ページの状態を読めませんでした(再適用の確認へ): {ex}");
            return null;
        }
    }

    // 「再適用の確認へ」(W-136)。揃える回があれば番組ページの確認の画面へ移る(入口が移す)。無ければ結果の 1 行を出す
    // (W-254 / W-255)。確認の画面から「戻る」でホームへ戻ると、要対応を計算し直す。
    private async Task ReapplyAsync()
    {
        if (!CanReapply || _savedReapply is not { } entry)
            return;

        _reapplyChecking = true;
        _reapplyResult = null;
        RefreshSavedReapply();
        SavedReapplyStart start;
        try
        {
            start = await entry.StartAsync();
        }
        catch (Exception ex)
        {
            // 入口は投げない約束だが、ボタンのハンドラから漏らさない。
            _logWarn($"管理画面: 再適用の確認へ進めませんでした: {ex}");
            start = SavedReapplyStart.Stay(ProgramsPageViewModel.SavedAborted($"{ex.Message}(gui.log 参照)"), isWarning: true);
        }
        finally
        {
            _reapplyChecking = false;
        }

        _reapplyResult = start.Message;
        _reapplyResultWarning = start.IsWarning;
        RefreshSavedReapply();
    }

    // W-132。取込元をエクスプローラーで開く(W-146 / W-147)。
    private async Task RevealFailureAsync(FailureAttentionRow row)
    {
        if (_pathActions is null)
            return;
        try
        {
            // 届かない NAS の存在確認は数秒かかりうる。待つ間も画面は操作できるので、途中であることを示す。
            FailureNote = IngestHistoryViewModel.Revealing;
            FailureNote = await _pathActions.RevealAsync(row.Failure.SourcePath);
        }
        catch (Exception ex)
        {
            // コマンドの例外を UI スレッドの未捕捉例外にしない(そちらは管理画面ごと閉じる最後の網)。
            _logWarn($"エクスプローラー起動でエラーが発生しました: {ex}");
            FailureNote = IngestHistoryViewModel.RevealFailed;
        }
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
        RefreshSavedReapply();

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
        if (_loadHistory is null)
        {
            _historyScan = scanCompleted;
            RecentRows = [];
            HistoryState = HistoryLoadState.Unavailable;
            OnPropertyChanged(nameof(RecentCaption));
            return;
        }

        // 読んでいる途中なら捨てる。_historyScan は読み始めた回のままなので、読み終えた後の Refresh で読み直す。
        if (_historyRunning)
            return;

        _historyScan = scanCompleted;
        _historyRunning = true;
        HistoryRefreshed = LoadRecentAsync(_loadHistory);
    }

    // 台帳は UI スレッドの外で読み、結果は呼んだスレッド(UI スレッド)で反映する(要対応の計算と同じ作り)。
    private async Task LoadRecentAsync(Func<IReadOnlyList<IngestHistoryRow>> load)
    {
        try
        {
            try
            {
                RecentRows = await Task.Run(() => load().Take(RecentCount).ToList());
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
        finally
        {
            _historyRunning = false;
        }
    }
}
