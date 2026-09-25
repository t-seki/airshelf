using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Radirec.Core.Configuration;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;
using Radirec.Hosting;
using Radirec.Hosting.Control;

namespace Radirec.Gui.Core.Management;

/// <summary>番組ページで出しているもの。</summary>
public enum ProgramsPageMode
{
    /// <summary>一覧(W-201〜W-210)。</summary>
    List,

    /// <summary>「アルバム名と統合」の編集フォーム(W-220〜W-227)。</summary>
    ProgramForm,

    /// <summary>「分けておく兄弟フォルダ」の編集フォーム(W-228)。</summary>
    KeepForm,

    /// <summary>確認の dry-run を待っている(W-240)。</summary>
    Checking,

    /// <summary>確認(変更前 → 変更後と、再適用の予定。W-241〜W-250)。</summary>
    Confirm,

    /// <summary>保存して再適用を実行している(W-270 / W-271)。</summary>
    Applying,

    /// <summary>結果(W-251 / W-272〜W-279)。</summary>
    Result,
}

/// <summary>
/// 管理画面の番組ページ(#154・requirements §10.3・ADR-0017)。<c>programs.json</c> の一覧・追加・編集・削除を画面の中の
/// 変更として溜め、「確認」で**保存前の設定のまま**再適用の dry-run を行って変更前 → 変更後を見せる。
/// 「保存して適用」(保存 → 再適用の実行 → 設定リロード)/「保存だけ」(保存 → 設定リロード)/「戻る」。
/// <para>
/// 保存は <see cref="ProgramsFileWriter"/>(ファイル全体の書き直し・バックアップ・読み込んだ後の変更の検出)で行う。
/// 再適用は確認のときの設定で実行し、確認の後に設定や状態が変わっていれば実行しない(W-278)。再適用の実行は
/// 今の設定(radirec.json)が確認のときと同じであることを前提にするので、設定リロードは実行の**後**に行う
/// (先に行うと、確認のときの設定と別物になり実行されない)。
/// </para>
/// <para>「確認」を押してから一覧か結果に戻るまでは <see cref="ProgramsApplyGate"/> を立て、ホームの再スキャンを止める(W-110)。</para>
/// </summary>
public sealed class ProgramsPageViewModel : ObservableObject
{
    // ---- 文言(docs/mockups/management-window.md)----

    /// <summary>W-201。</summary>
    public const string Intro = "番組の設定 — programs.json の中身です。保存するまでファイルは変わりません";

    /// <summary>W-211。</summary>
    public const string Unavailable = "設定を読めないため、番組の設定を出せません";

    /// <summary>W-212。</summary>
    public const string Unused = "radirec.json で programs.json を使わない設定になっています";

    /// <summary>W-213。</summary>
    public const string UnusedHint =
        "番組の設定を使うには、radirec.json の ProgramsFile にファイル名(例 programs.json)を書き、設定ページの「再読み込み」を押してください。";

    /// <summary>W-214。取り込みが止まっていて、確認(dry-run)を走らせられない。</summary>
    public const string ConfirmUnavailable = "取り込みが止まっているため、確認と保存はできません";

    /// <summary>W-222。</summary>
    public const string FoldersHint = "1 行に 1 フォルダ。上から順にパート番号 1, 2, … が付きます";

    /// <summary>W-223。</summary>
    public const string AlbumHint = "未登録のフォルダは、フォルダ名がそのままアルバム名になります";

    /// <summary>W-224。</summary>
    public const string StationRequired = "放送局を入れてください";

    /// <summary>W-224。</summary>
    public const string FoldersRequired = "フォルダを 1 つ以上入れてください";

    /// <summary>W-224。</summary>
    public const string AlbumRequired = "アルバム名を入れてください";

    /// <summary>W-240。</summary>
    public const string Checking = "再適用の内容を確かめています(取り込み中なら完了後)";

    /// <summary>W-251。</summary>
    public const string Aborted = "統合設定の再適用を中止しました";

    /// <summary>W-262 の「保存する」の後、再適用が返るまで(W-271)。</summary>
    public const string ApplyingText = "統合設定の再適用を実行しています";

    /// <summary>W-278。</summary>
    public const string Stale = "確認した後に設定が変わったため、実行していません。もう一度「確認」を押してください";

    /// <summary>W-279。</summary>
    public const string SavedOnly = "保存しました。揃っていない回は要対応の「再適用が要る番組」に出ます";

    /// <summary>W-276。</summary>
    public const string ReloadDiscards = "読み込み直すと、この画面での変更は消えます";

    /// <summary>確認がこれより長くかかるときだけ W-240 を出す(すぐ終わるなら出さない。トレイの再適用と同じ 1.5 秒)。</summary>
    public static readonly TimeSpan CheckingDelay = TimeSpan.FromMilliseconds(1500);

    private readonly IServiceControl? _control;
    private readonly Func<string?>? _locateProgramsFile;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _logWarn;
    private readonly Action<string> _logInfo;
    private readonly ProgramsApplyGate _applyGate;

    private readonly List<ProgramAlias> _removedPrograms = [];
    private readonly List<KeptApartSiblings> _removedKeeps = [];

    private ProgramsFileSnapshot? _snapshot;
    private bool _loaded;
    private bool _isUnavailable;
    private bool _isUnused;
    private string? _loadError;
    private bool _controlRunning;
    private bool _reapplyPending; // 保存したのに再適用を実行できなかった(確認し直せば変更が無くても実行できる)
    private ProgramsPageMode _mode = ProgramsPageMode.List;

    private ProgramEntryRow? _selectedProgram;
    private KeepSeparateRow? _selectedKeep;

    // 編集フォーム(編集している行。追加なら null)
    private ProgramEntryRow? _editingProgram;
    private KeepSeparateRow? _editingKeep;
    private string _formHeading = "";
    private string _formStation = "";
    private string _formFolders = "";
    private string _formAlbum = "";
    private string _formNote = "";
    private IReadOnlyList<string> _formErrors = [];

    // 確認
    private ReapplyReport? _plan;
    private ProgramMasterConfig? _planDraft;
    private string _checkingText = "";
    private string _diffBefore = "";
    private string _diffAfter = "";
    private string _confirmHeading = "";
    private string _confirmBody = "";
    private string? _saveOnlyNote;
    private bool _isCommentsPromptVisible;
    private string _commentsHeading = "";
    private string _commentsBody = "";
    private bool _pendingApplyAfterComments;

    // 結果
    private string? _savedMessage;
    private string _resultHeading = "";
    private string _resultBody = "";
    private bool _resultIsError;
    private bool _isConflict;

    /// <param name="control">取り込みワーカーの制御面。縮退(ワーカーが無い)なら null。</param>
    /// <param name="locateProgramsFile">
    /// いまの <c>programs.json</c> の場所。<c>radirec.json</c> で programs.json を使わない設定(<c>ProgramsFile</c> が空)なら
    /// null を返す。設定を読めていない(縮退。場所が決まらない)なら、この引数自体を null にする(W-211)。
    /// </param>
    /// <param name="applyGate">保存と適用の途中か(ホームの再スキャンと共有する)。</param>
    /// <param name="now">バックアップの名前に使う時刻(テスト用)。</param>
    public ProgramsPageViewModel(
        IServiceControl? control,
        Func<string?>? locateProgramsFile,
        ProgramsApplyGate applyGate,
        Action<string> logWarn,
        Action<string>? logInfo = null,
        Func<DateTime>? now = null)
    {
        _control = control;
        _locateProgramsFile = locateProgramsFile;
        _applyGate = applyGate;
        _logWarn = logWarn;
        _logInfo = logInfo ?? (_ => { });
        _now = now ?? (() => DateTime.Now);

        AddProgramCommand = new RelayCommand(AddProgram, () => IsListMode && IsReady);
        EditProgramCommand = new RelayCommand(EditProgram, () => IsListMode && SelectedProgram is not null);
        DeleteProgramCommand = new RelayCommand(DeleteProgram, () => IsListMode && SelectedProgram is not null);
        AddKeepCommand = new RelayCommand(AddKeep, () => IsListMode && IsReady);
        EditKeepCommand = new RelayCommand(EditKeep, () => IsListMode && SelectedKeep is not null);
        DeleteKeepCommand = new RelayCommand(DeleteKeep, () => IsListMode && SelectedKeep is not null);
        RevertCommand = new RelayCommand(Revert, () => IsListMode && HasChanges);
        CommitFormCommand = new RelayCommand(CommitForm);
        CancelFormCommand = new RelayCommand(() => Mode = ProgramsPageMode.List);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => CanConfirm);
        SaveAndApplyCommand = new AsyncRelayCommand(() => SaveAsync(apply: true), () => IsConfirmMode);
        SaveOnlyCommand = new AsyncRelayCommand(() => SaveAsync(apply: false), () => IsConfirmMode && HasChanges);
        BackCommand = new RelayCommand(BackToList);
        ProceedAfterCommentsCommand = new AsyncRelayCommand(ProceedAfterCommentsAsync);
        CancelCommentsCommand = new RelayCommand(() => IsCommentsPromptVisible = false);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync);

        RefreshStatus();
    }

    // ---- 一覧 ----

    public ObservableCollection<ProgramEntryRow> Programs { get; } = [];

    public ObservableCollection<KeepSeparateRow> KeepSeparate { get; } = [];

    public ProgramEntryRow? SelectedProgram
    {
        get => _selectedProgram;
        set
        {
            if (SetProperty(ref _selectedProgram, value))
            {
                EditProgramCommand.NotifyCanExecuteChanged();
                DeleteProgramCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public KeepSeparateRow? SelectedKeep
    {
        get => _selectedKeep;
        set
        {
            if (SetProperty(ref _selectedKeep, value))
            {
                EditKeepCommand.NotifyCanExecuteChanged();
                DeleteKeepCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>設定を読めていない(W-211)。</summary>
    public bool IsUnavailable { get => _isUnavailable; private set => SetProperty(ref _isUnavailable, value); }

    /// <summary>programs.json を使わない設定(W-212 / W-213)。</summary>
    public bool IsUnused { get => _isUnused; private set => SetProperty(ref _isUnused, value); }

    /// <summary>programs.json を読めなかった理由(壊れている等)。読めていれば null。</summary>
    public string? LoadError
    {
        get => _loadError;
        private set
        {
            if (SetProperty(ref _loadError, value))
                OnPropertyChanged(nameof(IsLoadFailed));
        }
    }

    public bool IsLoadFailed => LoadError is not null;

    /// <summary>programs.json を読めて、一覧を出せる。</summary>
    public bool IsReady => _snapshot is not null;

    /// <summary>W-210(各一覧の件数。空なら「まだありません」)。</summary>
    public string ProgramsCountText => CountText(Programs.Count);

    /// <summary>W-210。</summary>
    public string KeepCountText => CountText(KeepSeparate.Count);

    /// <summary>保存していない変更の数(追加・編集・削除したエントリ)。</summary>
    public int ChangeCount =>
        Programs.Count(r => r.IsChanged) + _removedPrograms.Count
        + KeepSeparate.Count(r => r.IsChanged) + _removedKeeps.Count;

    public bool HasChanges => ChangeCount > 0;

    /// <summary>W-208。変更が無ければ null。</summary>
    public string? ChangesText => HasChanges ? $"保存していない変更があります({ChangeCount} 件)" : null;

    /// <summary>「確認」(W-207)を押せるか。変更が無いとき・取り込みが止まっているときは押せない。</summary>
    public bool CanConfirm => IsListMode && IsReady && _controlRunning && (HasChanges || _reapplyPending);

    /// <summary>W-214。取り込みが止まっていて確認できないとき。それ以外は null。</summary>
    public string? ConfirmNote => IsReady && !_controlRunning ? ConfirmUnavailable : null;

    // ---- いま出しているもの ----

    public ProgramsPageMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value))
                return;
            OnPropertyChanged(nameof(IsListMode));
            OnPropertyChanged(nameof(IsFormMode));
            OnPropertyChanged(nameof(IsProgramFormMode));
            OnPropertyChanged(nameof(IsKeepFormMode));
            OnPropertyChanged(nameof(IsCheckingMode));
            OnPropertyChanged(nameof(IsConfirmMode));
            OnPropertyChanged(nameof(IsApplyingMode));
            OnPropertyChanged(nameof(IsResultMode));
            OnPropertyChanged(nameof(IsSavedMessageVisible));
            OnPropertyChanged(nameof(IsConfirmBackDefault));
            // 確認〜適用の途中はホームの再スキャンを止める(W-110)。一覧・フォーム・結果に戻ったら下ろす。
            _applyGate.IsBusy = value is ProgramsPageMode.Checking or ProgramsPageMode.Confirm or ProgramsPageMode.Applying;
            NotifyCommands();
        }
    }

    public bool IsListMode => Mode == ProgramsPageMode.List;
    public bool IsFormMode => Mode is ProgramsPageMode.ProgramForm or ProgramsPageMode.KeepForm;
    public bool IsProgramFormMode => Mode == ProgramsPageMode.ProgramForm;
    public bool IsKeepFormMode => Mode == ProgramsPageMode.KeepForm;
    public bool IsCheckingMode => Mode == ProgramsPageMode.Checking;
    public bool IsConfirmMode => Mode == ProgramsPageMode.Confirm;
    public bool IsApplyingMode => Mode == ProgramsPageMode.Applying;
    public bool IsResultMode => Mode == ProgramsPageMode.Result;

    // ---- 編集フォーム ----

    /// <summary>W-220 / W-228 / W-229。</summary>
    public string FormHeading { get => _formHeading; private set => SetProperty(ref _formHeading, value); }

    public string FormStation { get => _formStation; set => SetProperty(ref _formStation, value); }

    /// <summary>1 行に 1 フォルダ(W-222)。</summary>
    public string FormFolders { get => _formFolders; set => SetProperty(ref _formFolders, value); }

    public string FormAlbum { get => _formAlbum; set => SetProperty(ref _formAlbum, value); }

    public string FormNote { get => _formNote; set => SetProperty(ref _formNote, value); }

    /// <summary>W-224 / W-225。「決定」で入力を確かめた結果。</summary>
    public IReadOnlyList<string> FormErrors
    {
        get => _formErrors;
        private set
        {
            if (SetProperty(ref _formErrors, value))
                OnPropertyChanged(nameof(FormErrorText));
        }
    }

    /// <summary>W-224 / W-225 を 1 行ずつ。無ければ null。</summary>
    public string? FormErrorText => FormErrors.Count == 0 ? null : string.Join("\n", FormErrors);

    // ---- 確認 ----

    /// <summary>W-240。確認が <see cref="CheckingDelay"/> を超えてかかっているときだけ出す。</summary>
    public string CheckingText { get => _checkingText; private set => SetProperty(ref _checkingText, value); }

    /// <summary>W-241 の「変更前」。</summary>
    public string DiffBefore { get => _diffBefore; private set => SetProperty(ref _diffBefore, value); }

    /// <summary>W-241 の「変更後」。</summary>
    public string DiffAfter { get => _diffAfter; private set => SetProperty(ref _diffAfter, value); }

    /// <summary>W-242 / W-248。</summary>
    public string ConfirmHeading { get => _confirmHeading; private set => SetProperty(ref _confirmHeading, value); }

    /// <summary>W-243〜W-247 / W-248。</summary>
    public string ConfirmBody { get => _confirmBody; private set => SetProperty(ref _confirmBody, value); }

    /// <summary>W-250(揃っていない回があるときだけ)。</summary>
    public string? SaveOnlyNote { get => _saveOnlyNote; private set => SetProperty(ref _saveOnlyNote, value); }

    /// <summary>W-260〜W-262(初めて管理画面から保存するとき、programs.json にコメントがあれば一度だけ)。</summary>
    public bool IsCommentsPromptVisible
    {
        get => _isCommentsPromptVisible;
        private set
        {
            if (SetProperty(ref _isCommentsPromptVisible, value))
                OnPropertyChanged(nameof(IsConfirmBackDefault));
        }
    }

    /// <summary>
    /// 確認の「戻る」が既定のボタン(Enter / Esc)か。W-260 の確認を出している間は、そちらの「戻る」が既定になる
    /// (既定のボタンが 2 つあると Enter が取り合いになる)。
    /// </summary>
    public bool IsConfirmBackDefault => IsConfirmMode && !IsCommentsPromptVisible;

    /// <summary>W-260。</summary>
    public string CommentsHeading { get => _commentsHeading; private set => SetProperty(ref _commentsHeading, value); }

    /// <summary>W-261。</summary>
    public string CommentsBody { get => _commentsBody; private set => SetProperty(ref _commentsBody, value); }

    // ---- 結果 ----

    /// <summary>W-270(保存できたとき。再適用の実行中と結果に出す)。</summary>
    public string? SavedMessage
    {
        get => _savedMessage;
        private set
        {
            if (SetProperty(ref _savedMessage, value))
                OnPropertyChanged(nameof(IsSavedMessageVisible));
        }
    }

    public bool IsSavedMessageVisible => SavedMessage is not null && (IsApplyingMode || IsResultMode);

    /// <summary>W-251 / W-272 / W-275 / W-277 / W-278 / W-279 の見出し(1 行目)。</summary>
    public string ResultHeading { get => _resultHeading; private set => SetProperty(ref _resultHeading, value); }

    /// <summary>結果の本文(無ければ空)。</summary>
    public string ResultBody { get => _resultBody; private set => SetProperty(ref _resultBody, value); }

    /// <summary>結果が失敗・中止か(見た目を変える)。</summary>
    public bool ResultIsError { get => _resultIsError; private set => SetProperty(ref _resultIsError, value); }

    /// <summary>読み込んだ後に programs.json が変わっていた(W-275)。「読み込み直す」(W-276)だけを出す。</summary>
    public bool IsConflict
    {
        get => _isConflict;
        private set
        {
            if (SetProperty(ref _isConflict, value))
                OnPropertyChanged(nameof(CanReturnFromResult));
        }
    }

    /// <summary>結果から「戻る」で一覧に戻れるか(W-275 のときは読み込み直すしかない)。</summary>
    public bool CanReturnFromResult => !IsConflict;

    // ---- コマンド ----

    public RelayCommand AddProgramCommand { get; }
    public RelayCommand EditProgramCommand { get; }
    public RelayCommand DeleteProgramCommand { get; }
    public RelayCommand AddKeepCommand { get; }
    public RelayCommand EditKeepCommand { get; }
    public RelayCommand DeleteKeepCommand { get; }

    /// <summary>W-209。</summary>
    public RelayCommand RevertCommand { get; }

    /// <summary>W-227 の「決定」。</summary>
    public RelayCommand CommitFormCommand { get; }

    /// <summary>W-227 の「キャンセル」。</summary>
    public RelayCommand CancelFormCommand { get; }

    /// <summary>W-207。</summary>
    public AsyncRelayCommand ConfirmCommand { get; }

    /// <summary>W-249 の「保存して適用」。</summary>
    public AsyncRelayCommand SaveAndApplyCommand { get; }

    /// <summary>W-249 の「保存だけ」。</summary>
    public AsyncRelayCommand SaveOnlyCommand { get; }

    /// <summary>W-249 の「戻る」(確認から)と、結果から一覧へ戻る。</summary>
    public RelayCommand BackCommand { get; }

    /// <summary>W-262 の「保存する」。</summary>
    public AsyncRelayCommand ProceedAfterCommentsCommand { get; }

    /// <summary>W-262 の「戻る」。</summary>
    public RelayCommand CancelCommentsCommand { get; }

    /// <summary>W-276「読み込み直す」(この画面での変更は消える)。</summary>
    public AsyncRelayCommand ReloadCommand { get; }

    // ---- 読み込み ----

    /// <summary>
    /// ページを出したとき(管理画面の CurrentPage が番組になったとき)に呼ぶ。まだ読んでいない、または一覧で変更が無ければ
    /// programs.json を読み直す(エディタで直した・設定リロードで場所が変わった等に追従する)。
    /// </summary>
    public Task OnShownAsync()
    {
        RefreshStatus();
        if (_loaded && (!IsListMode || HasChanges || _reapplyPending))
            return Task.CompletedTask;
        return LoadAsync();
    }

    /// <summary>programs.json を読み、一覧を作り直す(この画面での変更は捨てる)。</summary>
    public async Task LoadAsync()
    {
        _loaded = true;
        if (_locateProgramsFile is null)
        {
            SetSnapshot(null);
            IsUnavailable = true;
            IsUnused = false;
            LoadError = null;
            return;
        }

        IsUnavailable = false;
        string? path;
        try
        {
            path = _locateProgramsFile();
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: programs.json の場所を決められませんでした: {ex}");
            SetSnapshot(null);
            LoadError = $"programs.json の場所を決められませんでした: {ex.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            SetSnapshot(null);
            IsUnused = true;
            LoadError = null;
            return;
        }

        IsUnused = false;
        try
        {
            // ProgramsFile が NAS を指していても UI スレッドを塞がない。
            var snapshot = await Task.Run(() => ProgramsFileWriter.Read(path));
            LoadError = null;
            SetSnapshot(snapshot);
        }
        catch (ProgramsFileException ex)
        {
            _logWarn($"管理画面: {path} を読めませんでした: {ex}");
            SetSnapshot(null);
            LoadError = ex.Message;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: {path} を読めませんでした: {ex}");
            SetSnapshot(null);
            LoadError = $"{Path.GetFileName(path)} を読めませんでした: {ex.Message}";
        }
    }

    /// <summary>取り込みが動いているか(確認を押せるか)を読み直す。管理画面のタイマーから 2 秒ごとに呼ぶ。</summary>
    public void RefreshStatus()
    {
        bool running;
        try
        {
            running = _control is not null && _control.GetStatus().Running;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 状態を読めませんでした(番組): {ex}");
            running = false;
        }

        if (_controlRunning == running)
            return;
        _controlRunning = running;
        OnPropertyChanged(nameof(ConfirmNote));
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private void SetSnapshot(ProgramsFileSnapshot? snapshot)
    {
        _snapshot = snapshot;
        _removedPrograms.Clear();
        _removedKeeps.Clear();
        _reapplyPending = false;
        Programs.Clear();
        KeepSeparate.Clear();
        if (snapshot is not null)
        {
            // 読み込んだ設定は書き換えない(画面で編集するのは写し)。変わったかどうかは写しと比べて判る。
            foreach (var entry in snapshot.Config.Programs)
                Programs.Add(new ProgramEntryRow(entry, ProgramsDraft.Copy(entry)));
            foreach (var entry in snapshot.Config.KeepSeparate)
                KeepSeparate.Add(new KeepSeparateRow(entry, ProgramsDraft.Copy(entry)));
        }
        SelectedProgram = null;
        SelectedKeep = null;
        Mode = ProgramsPageMode.List;
        OnPropertyChanged(nameof(IsReady));
        ListChanged();
    }

    // ---- 一覧の操作(ファイルには書かない。画面の中の変更として溜める)----

    private void AddProgram()
    {
        _editingProgram = null;
        FormHeading = "アルバム名と統合を追加"; // W-220
        FormStation = "";
        FormFolders = "";
        FormAlbum = "";
        FormNote = "";
        FormErrors = [];
        Mode = ProgramsPageMode.ProgramForm;
    }

    private void EditProgram()
    {
        if (SelectedProgram is not { } row)
            return;
        _editingProgram = row;
        FormHeading = "アルバム名と統合を編集"; // W-220
        FormStation = row.Station;
        FormFolders = row.Folders;
        FormAlbum = row.Album;
        FormNote = row.Note;
        FormErrors = [];
        Mode = ProgramsPageMode.ProgramForm;
    }

    private void DeleteProgram()
    {
        if (SelectedProgram is not { } row)
            return;
        Programs.Remove(row);
        if (row.Original is not null)
            _removedPrograms.Add(row.Original);
        SelectedProgram = null;
        ListChanged();
    }

    private void AddKeep()
    {
        _editingKeep = null;
        FormHeading = "分けておく兄弟フォルダを追加"; // W-229
        FormStation = "";
        FormFolders = "";
        FormAlbum = "";
        FormNote = "";
        FormErrors = [];
        Mode = ProgramsPageMode.KeepForm;
    }

    private void EditKeep()
    {
        if (SelectedKeep is not { } row)
            return;
        _editingKeep = row;
        FormHeading = "分けておく兄弟フォルダを編集"; // W-228
        FormStation = row.Station;
        FormFolders = string.Join("\n", row.Current.Match.Folders);
        FormAlbum = "";
        FormNote = row.Note;
        FormErrors = [];
        Mode = ProgramsPageMode.KeepForm;
    }

    private void DeleteKeep()
    {
        if (SelectedKeep is not { } row)
            return;
        KeepSeparate.Remove(row);
        if (row.Original is not null)
            _removedKeeps.Add(row.Original);
        SelectedKeep = null;
        ListChanged();
    }

    private void Revert()
    {
        if (_snapshot is { } snapshot)
            SetSnapshot(snapshot);
    }

    private void CommitForm()
    {
        if (Mode == ProgramsPageMode.ProgramForm)
            CommitProgramForm();
        else if (Mode == ProgramsPageMode.KeepForm)
            CommitKeepForm();
    }

    private void CommitProgramForm()
    {
        var station = FormStation.Trim();
        var folders = ProgramsDraft.ParseFolders(FormFolders);
        var album = FormAlbum.Trim();

        var errors = new List<string>();
        if (station.Length == 0)
            errors.Add(StationRequired);
        if (folders.Count == 0)
            errors.Add(FoldersRequired);
        if (album.Length == 0)
            errors.Add(AlbumRequired);
        if (station.Length > 0)
            errors.AddRange(DuplicateFolders(station, folders, _editingProgram));
        FormErrors = errors;
        if (errors.Count > 0)
            return;

        var entry = new ProgramAlias
        {
            Match = new ProgramMatch { Station = station, Folders = folders },
            Canonical = new ProgramCanonical { Album = album },
            Note = ProgramsDraft.ParseNote(FormNote),
        };

        if (_editingProgram is { } editing && Programs.IndexOf(editing) is var index and >= 0)
        {
            Programs[index] = new ProgramEntryRow(editing.Original, entry);
            SelectedProgram = Programs[index];
        }
        else
        {
            // 追加は末尾に置く(並べ替えは作らない。#154 の決定 6)。
            Programs.Add(new ProgramEntryRow(null, entry));
            SelectedProgram = Programs[^1];
        }

        Mode = ProgramsPageMode.List;
        ListChanged();
    }

    // W-225。同じ局の別のエントリに同じフォルダがあれば、どちらに解決されるかが曖昧になるので決定させない。
    // 照合は取り込みと同じキー(TextNormalizer.ToMatchKey)で行う。
    private List<string> DuplicateFolders(string station, IReadOnlyList<string> folders, ProgramEntryRow? editing)
    {
        var stationKey = TextNormalizer.ToMatchKey(station);
        var errors = new List<string>();
        foreach (var folder in folders)
        {
            var folderKey = TextNormalizer.ToMatchKey(folder);
            var other = Programs.FirstOrDefault(row =>
                !ReferenceEquals(row, editing)
                && TextNormalizer.ToMatchKey(row.Station) == stationKey
                && row.Current.Match.Folders.Any(f => TextNormalizer.ToMatchKey(f) == folderKey));
            if (other is not null)
                errors.Add($"フォルダ『{folder}』は『{other.Album}』の設定にも入っています");
        }
        return errors;
    }

    private void CommitKeepForm()
    {
        var station = FormStation.Trim();
        var folders = ProgramsDraft.ParseFolders(FormFolders);

        var errors = new List<string>();
        if (station.Length == 0)
            errors.Add(StationRequired);
        if (folders.Count == 0)
            errors.Add(FoldersRequired);
        FormErrors = errors;
        if (errors.Count > 0)
            return;

        var entry = new KeptApartSiblings
        {
            Match = new ProgramMatch { Station = station, Folders = folders },
            Note = ProgramsDraft.ParseNote(FormNote),
        };

        if (_editingKeep is { } editing && KeepSeparate.IndexOf(editing) is var index and >= 0)
        {
            KeepSeparate[index] = new KeepSeparateRow(editing.Original, entry);
            SelectedKeep = KeepSeparate[index];
        }
        else
        {
            KeepSeparate.Add(new KeepSeparateRow(null, entry));
            SelectedKeep = KeepSeparate[^1];
        }

        Mode = ProgramsPageMode.List;
        ListChanged();
    }

    // ---- 確認 ----

    private async Task ConfirmAsync()
    {
        if (!CanConfirm || _control is null || _snapshot is null)
            return;

        var draft = BuildDraft(_snapshot);
        CheckingText = "";
        Mode = ProgramsPageMode.Checking;

        ReapplyResult planned;
        try
        {
            var planTask = _control.PlanReapplyAsync(draft);
            // すぐ終わるなら W-240 は出さない(取り込み中の走査を待つときや NAS が遅いときだけ知らせる)。
            if (await Task.WhenAny(planTask, Task.Delay(CheckingDelay)) != planTask)
                CheckingText = Checking;
            planned = await planTask;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 再適用の確認に失敗しました: {ex}");
            planned = ReapplyResult.Failed($"再適用の確認に失敗しました: {ex.Message}");
        }

        if (planned.Report is not { } plan)
        {
            ShowResult(Aborted, planned.Error ?? "理由不明", isError: true);
            return;
        }

        _plan = plan;
        _planDraft = draft;
        (DiffBefore, DiffAfter) = Diff();

        var text = plan.IsAligned
            ? ReapplyDialogFormatter.BuildAligned(plan, ReapplyBasis.Draft)
            : ReapplyDialogFormatter.BuildConfirmation(plan, ReapplyBasis.Draft);
        ConfirmHeading = text.Heading;
        ConfirmBody = text.Body;
        SaveOnlyNote = plan.IsAligned
            ? null
            : $"「保存だけ」にすると、揃っていない {ReapplyDialogFormatter.ChangeCount(plan)} 回は要対応の「再適用が要る番組」に残ります"; // W-250
        IsCommentsPromptVisible = false;
        Mode = ProgramsPageMode.Confirm;
    }

    private ProgramMasterConfig BuildDraft(ProgramsFileSnapshot snapshot) => new()
    {
        Defaults = snapshot.Config.Defaults,
        Programs = Programs.Select(r => ProgramsDraft.Copy(r.Current)).ToList(),
        KeepSeparate = KeepSeparate.Select(r => ProgramsDraft.Copy(r.Current)).ToList(),
    };

    // W-241。変更前 = 消した・変えたエントリの元、変更後 = 足した・変えたエントリの今。
    private (string Before, string After) Diff()
    {
        var before = new List<object>();
        var after = new List<object>();

        foreach (var row in Programs.Where(r => r.IsChanged))
        {
            if (row.Original is not null)
                before.Add(row.Original);
            after.Add(row.Current);
        }
        before.AddRange(_removedPrograms);

        foreach (var row in KeepSeparate.Where(r => r.IsChanged))
        {
            if (row.Original is not null)
                before.Add(row.Original);
            after.Add(row.Current);
        }
        before.AddRange(_removedKeeps);

        return (ProgramsDraft.Describe(before), ProgramsDraft.Describe(after));
    }

    private void BackToList()
    {
        IsCommentsPromptVisible = false;
        IsConflict = false;
        Mode = ProgramsPageMode.List;
    }

    // ---- 保存 ----

    private Task SaveAsync(bool apply)
    {
        if (!IsConfirmMode || _snapshot is null)
            return Task.CompletedTask;

        // 初めて管理画面から保存するとき(= ファイルにまだコメントがある)だけ、コメントが消えることを確かめる(ADR-0017)。
        if (HasChanges && _snapshot.CommentCount > 0)
        {
            var name = Path.GetFileName(_snapshot.Path);
            CommentsHeading = $"コメントが {_snapshot.CommentCount} 件消えます(.bak に残ります)"; // W-260
            CommentsBody = "管理画面から保存すると、programs.json 全体を書き直すため、// のコメントは残りません。" +
                           $"保存の前に、同じフォルダに {name}.bak-日時 を作ります。これからのメモは各行の「メモ」欄に書けます。"; // W-261
            _pendingApplyAfterComments = apply;
            IsCommentsPromptVisible = true;
            return Task.CompletedTask;
        }

        return SaveCoreAsync(apply);
    }

    private Task ProceedAfterCommentsAsync()
    {
        if (!IsCommentsPromptVisible)
            return Task.CompletedTask;
        IsCommentsPromptVisible = false;
        return SaveCoreAsync(_pendingApplyAfterComments);
    }

    private async Task SaveCoreAsync(bool apply)
    {
        if (_snapshot is not { } snapshot || _plan is not { } plan || _planDraft is not { } draft)
            return;

        var name = Path.GetFileName(snapshot.Path);
        SavedMessage = null;
        var saved = false;

        // 変更が無い(保存した後に再適用だけをやり直す)ときは書かない。
        if (HasChanges)
        {
            Mode = ProgramsPageMode.Applying;
            try
            {
                var now = _now();
                // 確認で見せた設定(draft)をそのまま書く。確認の後に一覧は変えられない(確認の画面では編集できない)。
                var backup = await Task.Run(() => ProgramsFileWriter.Write(snapshot, draft, now));
                SavedMessage = backup is null
                    ? $"{name} を保存しました"
                    : $"{name} を保存しました(バックアップ: {Path.GetFileName(backup)})"; // W-270
                _logInfo($"管理画面: {snapshot.Path} を保存しました(バックアップ: {backup ?? "なし(新しく作った)"})");
                saved = true;
            }
            catch (ProgramsFileChangedException ex)
            {
                _logWarn($"管理画面: {snapshot.Path} を保存しませんでした: {ex.Message}");
                IsConflict = true;
                ShowResult($"{name} は読み込んだ後に変更されています(エディタで直した等)。保存していません。", "", isError: true); // W-275
                return;
            }
            catch (Exception ex)
            {
                // バックアップか置き換えで失敗した。置き換えは一時ファイル経由なので programs.json は元のまま。
                _logWarn($"管理画面: {snapshot.Path} に書き込めませんでした: {ex}");
                ShowResult($"書き込みに失敗しました: {ex.Message}", $"{name} は元のままです。", isError: true); // W-277
                return;
            }
        }

        if (!apply)
        {
            Reload();
            await ReloadSnapshotAfterSaveAsync(snapshot.Path, reapplyPending: false);
            // 「保存だけ」の結果は 1 行で出す。揃っていない回があれば W-279、揃っていれば保存できたこと(W-270)。
            var savedText = SavedMessage ?? $"{name} を保存しました";
            SavedMessage = null;
            ShowResult(plan.IsAligned ? savedText : SavedOnly, "", isError: false);
            return;
        }

        Mode = ProgramsPageMode.Applying;
        ReapplyResult applied;
        try
        {
            applied = await _control!.ApplyReapplyAsync(plan);
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 再適用の実行に失敗しました: {ex}");
            applied = ReapplyResult.Failed($"再適用に失敗しました: {ex.Message}");
        }

        // 取り込みも保存した設定で動くよう、再適用の後に設定リロードする(先にすると確認のときの設定と別物になり実行されない)。
        if (saved)
            Reload();

        var done = applied.Report is not null;
        await ReloadSnapshotAfterSaveAsync(snapshot.Path, reapplyPending: !done);

        if (applied.IsStale)
            ShowResult(Stale, "", isError: true);
        else if (applied.Report is { } result)
        {
            var text = ReapplyDialogFormatter.BuildCompletion(result);
            ShowResult(text.Heading, text.Body, isError: result.Count(ReapplyAction.Failed) > 0);
        }
        else
            ShowResult(Aborted, applied.Error ?? "理由不明", isError: true);
    }

    private void Reload()
    {
        try
        {
            var outcome = _control?.RequestReload();
            if (outcome is { Accepted: false })
                _logWarn($"管理画面: 保存した後の設定リロードは拒否されました: {outcome.Error}");
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 保存した後の設定リロードに失敗しました: {ex}");
        }
    }

    // 保存した後は、ファイルの中身を読み直して一覧を作り直す(次に保存するときの「読み込んだ後に変わったか」の基準も新しくする)。
    private async Task ReloadSnapshotAfterSaveAsync(string path, bool reapplyPending)
    {
        try
        {
            var snapshot = await Task.Run(() => ProgramsFileWriter.Read(path));
            SetSnapshot(snapshot);
            _reapplyPending = reapplyPending;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 保存した後に {path} を読み直せませんでした: {ex}");
            SetSnapshot(null);
            LoadError = ex.Message;
        }
        NotifyCommands();
    }

    private async Task ReloadAsync()
    {
        IsConflict = false;
        await LoadAsync();
    }

    private void ShowResult(string heading, string body, bool isError)
    {
        ResultHeading = heading;
        ResultBody = body;
        ResultIsError = isError;
        IsCommentsPromptVisible = false;
        Mode = ProgramsPageMode.Result;
    }

    private void ListChanged()
    {
        OnPropertyChanged(nameof(ProgramsCountText));
        OnPropertyChanged(nameof(KeepCountText));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(ChangesText));
        OnPropertyChanged(nameof(ConfirmNote));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanConfirm));
        AddProgramCommand.NotifyCanExecuteChanged();
        EditProgramCommand.NotifyCanExecuteChanged();
        DeleteProgramCommand.NotifyCanExecuteChanged();
        AddKeepCommand.NotifyCanExecuteChanged();
        EditKeepCommand.NotifyCanExecuteChanged();
        DeleteKeepCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        ConfirmCommand.NotifyCanExecuteChanged();
        SaveAndApplyCommand.NotifyCanExecuteChanged();
        SaveOnlyCommand.NotifyCanExecuteChanged();
    }

    // W-210
    private static string CountText(int count) => count == 0 ? "まだありません" : $"{count} 件";
}
