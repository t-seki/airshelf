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

    /// <summary>
    /// 確認(変更前 → 変更後と、再適用の予定。W-241〜W-250)。保存済みの設定で確かめたとき(<see cref="ProgramsPageViewModel.IsSavedBasis"/>。
    /// #164)は、変更前 → 変更後を出さず、W-252 / W-253 とボタン W-312(<c>適用</c> / <c>戻る</c>)にする。
    /// </summary>
    Confirm,

    /// <summary>保存して再適用を実行している(W-270 / W-271)。保存済みの設定で確かめたときは、保存せずに実行している(W-271)。</summary>
    Applying,

    /// <summary>結果(W-251 / W-272〜W-279)。</summary>
    Result,
}

/// <summary>
/// 管理画面の番組ページ(#154・requirements §10.3・ADR-0017)。<c>programs.json</c> の一覧・追加・編集・削除を画面の中の
/// 変更として溜め、「確認」で**保存前の設定のまま**再適用の dry-run を行って変更前 → 変更後を見せる。
/// 「保存して適用」(保存 → 設定リロード → 再適用の実行。ADR-0016)/「保存だけ」(保存 → 設定リロード)/「戻る」。
/// <para>
/// 保存は <see cref="ProgramsFileWriter"/>(ファイル全体の書き直し・バックアップ・読み込んだ後の変更の検出)で行う。
/// 設定リロードと再適用の実行は、worker の 1 つのジョブの中で続けて行う(<see cref="IServiceControl.ReloadAndApplyReapplyAsync"/>。
/// 間に取り込みの走査を挟まない)。リロードに失敗したら再適用はせず、理由を出す(W-251)。読み直した設定が確認のときの
/// 下書きと値で違う、または予定が変わっていれば実行しない(W-278)。保存できたのに実行できなかったときは、変更が無くても
/// 「確認」から実行し直せる(リロードも試し直す)。
/// </para>
/// <para>「確認」を押してから一覧か結果に戻るまでは <see cref="ProgramsApplyGate"/> を立て、ホームの再スキャンを止める(W-110)。</para>
/// <para>
/// もう 1 つの入口として、**保存済みの設定**で再適用を確かめる(<see cref="ConfirmSavedAsync"/>。#164)。設定ページの stations.json の
/// 「再読み込み」と、要対応の「再適用の確認へ」(W-136)から来る。設定リロード → dry-run(<see cref="IServiceControl.PlanReapplyAsync()"/>)
/// を行い、同じ確認の画面を W-252 / W-253 / W-312 の形で出す。「適用」は <see cref="IServiceControl.ApplyReapplyAsync"/> で実行し
/// (確かめたときの設定・予定と違えば実行しない。W-278)、「戻る」は来たページへ戻る。揃える回が無い・確かめられないときは、確認の画面を
/// 出さずに、来たページに出す 1 行(W-254 / W-255)を返す。
/// </para>
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

    /// <summary>W-278 の、保存済みの設定で確かめたときの形(番組ページに「確認」が無いので、来たページからやり直す)。</summary>
    public const string SavedStale = "確認した後に設定が変わったため、実行していません。もう一度確かめ直してください";

    /// <summary>
    /// W-256。番組ページに保存していない作業があるので、保存済みの設定で再適用を確かめられない(確かめると確認の画面に
    /// 替わり、下書きが見えなくなるため。下書きは消さない)。
    /// </summary>
    public const string SavedConfirmBlocked = "番組ページに保存していない変更があるため、再適用を確かめられません";

    /// <summary>W-279。</summary>
    public const string SavedOnly = "保存しました。揃っていない回は要対応の「再適用が要る番組」に出ます";

    /// <summary>W-276。</summary>
    public const string ReloadDiscards = "読み込み直すと、この画面での変更は消えます";

    /// <summary>W-226。要対応の「統合する」「分けておく」から下書きを作ったとき、編集フォームの上に出す。</summary>
    public const string DraftFromAttention = "要対応の兄弟フォルダから下書きを作りました。アルバム名を確かめてください";

    /// <summary>W-226 の「分けておく」の形(分けておく兄弟フォルダにはアルバム名が無い)。</summary>
    public const string KeepDraftFromAttention = "要対応の兄弟フォルダから下書きを作りました。フォルダの組を確かめてください";

    /// <summary>W-138。番組ページに保存していない作業があるので、要対応の「分けておく」で programs.json を書けない。</summary>
    public const string KeepApartBlocked = "番組ページに保存していない変更があるため、分けておけません";

    /// <summary>確認がこれより長くかかるときだけ W-240 を出す(すぐ終わるなら出さない。トレイの再適用と同じ 1.5 秒)。</summary>
    public static readonly TimeSpan CheckingDelay = TimeSpan.FromMilliseconds(1500);

    private readonly IServiceControl? _control;
    private readonly Func<string?>? _locateProgramsFile;
    private readonly Func<DateTime> _now;
    private readonly Func<string, Task<ProgramsFileSnapshot>> _readForLoad;
    private readonly Func<string, Task<byte[]?>> _readForCheck;
    private readonly Action<string> _logWarn;
    private readonly Action<string> _logInfo;
    private readonly ProgramsApplyGate _applyGate;
    private readonly Action<ManagementPage> _navigate;

    private readonly List<ProgramAlias> _removedPrograms = [];
    private readonly List<KeptApartSiblings> _removedKeeps = [];

    private ProgramsFileSnapshot? _snapshot;
    private bool _loaded;
    private Task? _loading; // 走っている読み込み(LoadAsync を 1 本にまとめる)
    private bool _discardWorkOnLoad; // 走っている読み込みは、作業が残っていても一覧を作り直す(W-276 の「読み込み直す」)
    private int _snapshotVersion; // SetSnapshot で一覧を作り直した回数(読んでいるあいだに、ほかの経路が作り直したかを見る)
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
    private string? _formNotice;

    // 確認
    private ReapplyReport? _plan;
    private ProgramMasterConfig? _planDraft;
    private bool _isSavedBasis;                          // 保存済みの設定で確かめている(W-252 / W-253。#164)
    private ManagementPage _savedOrigin = ManagementPage.Programs; // その確認に来たページ(「戻る」で戻る先)
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
    /// <param name="navigate">
    /// 管理画面のページを移る。保存済みの設定での確認(<see cref="ConfirmSavedAsync"/>)の「戻る」で、来たページへ戻るのに使う。
    /// </param>
    /// <param name="readForLoad">
    /// <see cref="LoadAsync"/> が programs.json を読む(テスト用。読み込みを止めておき、読み終わる順序をテストで決める)。
    /// 既定は <see cref="ProgramsFileWriter.Read"/> をスレッドプールで読む。
    /// </param>
    /// <param name="readForCheck">
    /// 読み込んだときから programs.json が変わったかを確かめるとき(「戻る」・ページを出したとき等)に、いまの中身を読む
    /// (テスト用。確かめる処理を止めておき、確かめ終わる順序をテストで決める)。ファイルが無ければ null。
    /// 既定はスレッドプールで読む。
    /// </param>
    public ProgramsPageViewModel(
        IServiceControl? control,
        Func<string?>? locateProgramsFile,
        ProgramsApplyGate applyGate,
        Action<string> logWarn,
        Action<string>? logInfo = null,
        Func<DateTime>? now = null,
        Action<ManagementPage>? navigate = null,
        Func<string, Task<ProgramsFileSnapshot>>? readForLoad = null,
        Func<string, Task<byte[]?>>? readForCheck = null)
    {
        _control = control;
        _locateProgramsFile = locateProgramsFile;
        _applyGate = applyGate;
        _logWarn = logWarn;
        _logInfo = logInfo ?? (_ => { });
        _now = now ?? (() => DateTime.Now);
        // ProgramsFile が NAS を指していても UI スレッドを塞がない。
        _readForLoad = readForLoad ?? (path => Task.Run(() => ProgramsFileWriter.Read(path)));
        _readForCheck = readForCheck ?? (path => Task.Run(() => File.Exists(path) ? File.ReadAllBytes(path) : null));
        _navigate = navigate ?? (_ => { });

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
        SaveAndApplyCommand = new AsyncRelayCommand(() => SaveAsync(apply: true), () => IsConfirmMode && !IsSavedBasis);
        SaveOnlyCommand = new AsyncRelayCommand(() => SaveAsync(apply: false), () => IsConfirmMode && !IsSavedBasis && HasChanges);
        ApplySavedCommand = new AsyncRelayCommand(ApplySavedAsync, () => IsConfirmMode && IsSavedBasis);
        BackCommand = new AsyncRelayCommand(BackToListAsync);
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
            OnPropertyChanged(nameof(IsResultBackDefault));
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

    /// <summary>W-226(要対応から下書きを作ったとき)。それ以外は null。</summary>
    public string? FormNotice
    {
        get => _formNotice;
        private set
        {
            if (SetProperty(ref _formNotice, value))
                OnPropertyChanged(nameof(IsFormNoticeVisible));
        }
    }

    public bool IsFormNoticeVisible => FormNotice is not null;

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

    /// <summary>
    /// 保存済みの設定で確かめている(設定ページの stations.json の「再読み込み」・要対応の「再適用の確認へ」から来た。#164)。
    /// 確認の画面は W-252 / W-253 の形になり、変更前 → 変更後(W-241)は出さず、ボタンは W-312(<c>適用</c> / <c>戻る</c>)になる。
    /// </summary>
    public bool IsSavedBasis
    {
        get => _isSavedBasis;
        private set
        {
            if (!SetProperty(ref _isSavedBasis, value))
                return;
            OnPropertyChanged(nameof(IsDraftBasis));
            NotifyCommands();
        }
    }

    /// <summary>番組ページで編集した設定(保存前)で確かめている。<see cref="IsSavedBasis"/> の逆。</summary>
    public bool IsDraftBasis => !IsSavedBasis;

    /// <summary>
    /// 保存済みの設定での確認(<see cref="ConfirmSavedAsync"/>)を始められない理由(W-256)。始められるなら null。
    /// 入口のボタンは押せなくし、理由は入口ごとの場所に出す(設定ページは stations.json の段、ホームは「再適用の確認へ」
    /// 〈W-136〉の下)。
    /// </summary>
    public string? SavedConfirmBlockedReason => HasUnsavedWork ? SavedConfirmBlocked : null;

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
            {
                OnPropertyChanged(nameof(CanReturnFromResult));
                OnPropertyChanged(nameof(IsResultBackDefault));
            }
        }
    }

    /// <summary>結果から「戻る」で一覧に戻れるか(W-275 のときは読み込み直すしかない)。</summary>
    public bool CanReturnFromResult => !IsConflict;

    /// <summary>
    /// 結果の「戻る」が既定のボタン(Enter)か。見えているときだけ(W-275 で隠しているときに Enter で押せないように)。
    /// </summary>
    public bool IsResultBackDefault => IsResultMode && CanReturnFromResult;

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

    /// <summary>W-312 の「適用」(保存済みの設定で確かめた再適用を実行する)。</summary>
    public AsyncRelayCommand ApplySavedCommand { get; }

    /// <summary>
    /// W-249 / W-312 の「戻る」(確認から)と、結果から戻る。番組ページで編集した設定の確認なら一覧へ、保存済みの設定の確認なら
    /// 来たページ(設定・ホーム)へ戻る。
    /// </summary>
    public AsyncRelayCommand BackCommand { get; }

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
    /// 保存したのに再適用を実行できなかった(<see cref="_reapplyPending"/>)ときは、ファイルがエディタで変わっていたときだけ
    /// 読み直す(変わっていなければ読み直さず、「確認」から中止された再適用を続けられるようにする)。
    /// 結果の画面のままなら、ファイルが変わっていたときだけ読み直す(ホームの要対応の「分けておく」で書いた等。古い一覧のまま
    /// 次に保存すると W-275 で弾かれるため)。変わっていなければ結果を出したままにする。
    /// </summary>
    public async Task OnShownAsync()
    {
        RefreshStatus();
        if (!_loaded)
        {
            await LoadAsync();
            return;
        }
        if (!CanReplaceWithLoaded)
            return;
        // 確かめている間に編集を始めていたら、LoadAsync が読み直さない(結果の画面のままなら、変わったので読み直す)。
        if ((_reapplyPending || IsResultMode) && !await FileChangedAsync())
            return;
        await LoadAsync();
    }

    // 読み込んだときから programs.json が変わったか(読めない・消えた等も「変わった」として読み直させる)。
    private async Task<bool> FileChangedAsync()
    {
        if (_snapshot is not { } snapshot)
            return true;
        try
        {
            var current = await _readForCheck(snapshot.Path);
            return (current, snapshot.Original) switch
            {
                (null, null) => false,
                ({ } c, { } o) => !c.AsSpan().SequenceEqual(o),
                _ => true,
            };
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: {snapshot.Path} を確かめられませんでした: {ex}");
            return true;
        }
    }

    /// <summary>
    /// programs.json を読み、一覧を作り直す。読み込みが走っていれば、新しく始めずにそれを待つ
    /// (2 本走ると、後から終わった方が、その間に始めた確認の画面を一覧に戻してしまうため。#164)。
    /// 作り直すのは、読み直しても作業が消えないとき(<see cref="CanReplaceWithLoaded"/>)だけ。読む前と、読み終えた後に確かめる
    /// (読んでいるあいだに編集・確認を始めていた、または保存してほかの経路が一覧を作り直していたら、読んだ結果を捨てる。#185)。
    /// </summary>
    public Task LoadAsync() => LoadAsync(discardWork: false);

    // discardWork: 利用者が変更を捨てると決めて読み直す(「読み込み直す」W-276)。作業が残っていても作り直す。
    // 読み込みが走っていれば、それに「作り直す」を伝えて待つ(読み終えた後の確かめで使う)。
    private Task LoadAsync(bool discardWork)
    {
        if (discardWork)
            _discardWorkOnLoad = true;
        if (_loading is { IsCompleted: false } running)
            return running;
        return _loading = LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            await LoadCoreAsyncInner();
        }
        finally
        {
            _discardWorkOnLoad = false;
        }
    }

    // 読み直しても作業が消えないか:一覧か結果の画面で、保存していない変更が無い。編集フォーム・確認・適用中の作業と、
    // 一覧の変更は消さない。結果の画面は保存を済ませた後なので読み直してよい(OnShownAsync の「結果の画面のまま」)。
    // 読み始める前に確かめる。読み終えたときは MayReplaceAfterRead で確かめ直す。
    private bool CanReplaceWithLoaded => !HasChanges && (IsListMode || IsResultMode);

    // 読み始めてよいか。まだ一度も読んでいなければ、作り直すものが無いので読む。
    private bool MayStartLoad => !_loaded || _discardWorkOnLoad || CanReplaceWithLoaded;

    // 読み終えたときに、読んだ結果(読めなかったことも)で一覧を作り直してよいか(#185)。version は読み始めたときの値。
    private bool MayReplaceAfterRead(int version)
    {
        // ほかの経路が、読んでいるあいだにファイルを読んで一覧を作り直した(番号を進めるのはそういう経路だけ。SetSnapshot)。
        // 読んでいるあいだに「確認 → 保存して適用 / 保存だけ」まで進むと、
        // 保存した後の読み直し(ReloadSnapshotAfterSaveAsync)が作り直してから結果の画面になる。その読み直しは書いた後の
        // ファイルを読んでいて、こちらの読み込み(書く前に始めたかもしれない)より古くない。作り直すと、保存した番組が消えた
        // ように見え、次の保存の基準(_snapshot.Original)も古くなって W-275 を誤って出す。
        // 「読み込み直す」(W-276)でも捨てる:変更を捨ててファイルの今の中身に合わせる、という利用者の決めたことは、
        // ほかの経路の作り直しで済んでいる。
        if (_snapshotVersion != version)
            return false;
        if (_discardWorkOnLoad)
            return true;
        // 結果の画面は、ここでは読み始める前から出ていたものだけ。読んでいるあいだに結果の画面になる経路は、保存・適用
        // (上の作り直しを通る)と、保存済みの設定での確認(ConfirmSavedAsync が走っている読み込みを待ってから始める)だけ。
        return CanReplaceWithLoaded;
    }

    private async Task LoadCoreAsyncInner()
    {
        if (!MayStartLoad)
            return;
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
            LoadError = LocateFailed(ex.Message); // W-215
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
        var version = _snapshotVersion;
        try
        {
            var snapshot = await _readForLoad(path);
            if (!KeepWorkOnLoad(path, version))
            {
                LoadError = null;
                SetSnapshot(snapshot);
            }
        }
        catch (ProgramsFileException ex)
        {
            // W-216(ProgramsFileWriter.Read の文言:「{ファイル名} を読めませんでした: …」/「… を読めません(JSON が壊れています): …」)
            _logWarn($"管理画面: {path} を読めませんでした: {ex}");
            if (!KeepWorkOnLoad(path, version))
            {
                SetSnapshot(null);
                LoadError = ex.Message;
            }
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: {path} を読めませんでした: {ex}");
            if (!KeepWorkOnLoad(path, version))
            {
                SetSnapshot(null);
                LoadError = ReadFailed(Path.GetFileName(path), ex.Message); // W-216
            }
        }
    }

    // 読んでいるあいだに編集・確認・保存が進んでいたら、読んだ結果(または読めなかったこと)で一覧を作り直さない(#185)。
    // 作業を残したときは、読み込んだときの基準(スナップショット)が前のままなので、次に保存するときに programs.json が
    // 変わっていれば W-275 で分かる。
    private bool KeepWorkOnLoad(string path, int version)
    {
        if (MayReplaceAfterRead(version))
            return false;
        _logInfo(_snapshotVersion != version
            ? $"管理画面: {path} を読んでいるあいだに番組ページの一覧が作り直されたので、読んだ結果を捨てました"
            : $"管理画面: {path} を読んでいるあいだに番組ページの作業が始まったので、読んだ結果で一覧を作り直しませんでした");
        return true;
    }

    /// <summary>W-215。programs.json の場所を決められなかった(状態を読めない等)。</summary>
    public static string LocateFailed(string reason) => $"programs.json の場所を決められませんでした: {reason}";

    /// <summary>
    /// W-216。programs.json を読めなかった(<see cref="ProgramsFileWriter.Read"/> と同じ形。壊れているときは
    /// <c>{ファイル名} を読めません(JSON が壊れています): {理由}</c>)。
    /// </summary>
    public static string ReadFailed(string fileName, string reason) => $"{fileName} を読めませんでした: {reason}";

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

    // programs.json を読んだ(読もうとした)結果で、基準のスナップショットと一覧を作り直す。世代の番号を進める
    // (走っている読み込みが、読み終えたときに「ほかの経路がファイルを読んで作り直した」と分かるように。#185)。
    // 呼ぶのは、ファイルの今の状態を見て作り直すところだけ:
    // - LoadCoreAsyncInner:読み込み(設定を読めない・場所を決められない・使わない設定・読んだ・読めなかった)
    // - ReloadSnapshotAfterSaveAsync:保存した後の読み直し(読めなかったときも)
    // ファイルを読まずに手元のスナップショットで作り直す「元に戻す」(W-209)は RebuildList を呼び、番号を進めない
    // (進めると、走っている読み込みが読んだ新しい中身が捨てられ、一覧が古いままになる)。
    private void SetSnapshot(ProgramsFileSnapshot? snapshot)
    {
        _snapshotVersion++;
        RebuildList(snapshot);
    }

    // 基準のスナップショットを snapshot にし、一覧をその写しで作り直す(画面での変更は捨てる)。
    private void RebuildList(ProgramsFileSnapshot? snapshot)
    {
        _snapshot = snapshot;
        _removedPrograms.Clear();
        _removedKeeps.Clear();
        _reapplyPending = false;
        IsSavedBasis = false; // 一覧に戻すので、保存済みの設定での確認は終わる
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
        FormNotice = null;
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
        FormNotice = null;
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
        FormNotice = null;
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
        FormNotice = null;
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
        // ファイルは読まない(読み込んだときの基準に戻すだけ)。走っている読み込みは、読み終えたら今までどおり作り直す。
        if (_snapshot is { } snapshot)
            RebuildList(snapshot);
    }

    // ---- 要対応の「統合する」「分けておく」(#155・ADR-0018)----

    /// <summary>
    /// 保存していない作業があるか(一覧で変更した・編集フォームや確認の途中)。あるときは、要対応の「分けておく」で
    /// programs.json を書かない(書くと、この画面の下書きを保存するときに W-275 で保存できなくなるため)。
    /// </summary>
    public bool HasUnsavedWork => _loaded && (HasChanges || !(IsListMode || IsResultMode));

    /// <summary>
    /// 要対応の「統合する」(W-126)。兄弟フォルダの組から下書きを作り、編集フォームに入れて出す(ファイルには書かない。
    /// 「決定」→「確認」→「保存して適用」は利用者が押す)。
    /// <list type="bullet">
    /// <item>統合番組がまだ無い組:新しいエントリ(局・組のフォルダをパートの順に・アルバム名は印を除いたフォルダ名。#144 の提案と同じ)</item>
    /// <item>組の一部がすでに統合番組に入っている(W-124):その統合番組を編集し、入っていないフォルダを <c>Folders</c> の末尾に足す
    /// (並べ直さない。すでに取り込んだ回のパート番号を動かさないため)</item>
    /// </list>
    /// 「統合設定が無い」は、この画面の下書き(保存していない変更を含む)で決め直す。
    /// </summary>
    /// <returns>
    /// 下書きを出したか。一覧を出せない(読めない・使わない設定)・一覧以外の画面の途中・下書きではもう統合済みなら false
    /// (結果の画面からは、先に <see cref="LeaveResultForDraftAsync"/> で一覧に戻しておく)。
    /// </returns>
    public bool DraftIntegration(SiblingFolderGroup group)
    {
        if (!IsListMode || !IsReady)
            return false;

        var draft = new ProgramMasterConfig { Programs = Programs.Select(r => r.Current).ToList() };
        if (SiblingProgramProposals.Build([group], new ProgramResolver(draft)).FirstOrDefault() is not { } proposal)
            return false;

        var joined = proposal.JoinsExisting ? FindMergedRow(proposal, group) : null;
        if (joined is not null)
        {
            _editingProgram = joined;
            FormHeading = "アルバム名と統合を編集"; // W-220
            FormStation = joined.Station;
            FormFolders = string.Join("\n", joined.Current.Match.Folders.Concat(proposal.Folders));
            FormAlbum = joined.Album;
            FormNote = joined.Note;
        }
        else
        {
            _editingProgram = null;
            FormHeading = "アルバム名と統合を追加"; // W-220
            FormStation = proposal.Station;
            FormFolders = string.Join("\n", proposal.Folders);
            FormAlbum = proposal.Album;
            FormNote = "";
        }
        FormErrors = [];
        FormNotice = DraftFromAttention; // W-226
        Mode = ProgramsPageMode.ProgramForm;
        return true;
    }

    /// <summary>
    /// 要対応の「分けておく」(W-127)を、編集フォームの下書きとして出す(programs.json にコメントがあり、その場では書かないとき。
    /// 保存の前にコメントが消えることを確かめる W-260 を通すため)。
    /// </summary>
    /// <returns>
    /// 下書きを出したか。一覧を出せない・一覧以外の画面の途中なら false(結果の画面からは、先に
    /// <see cref="LeaveResultForDraftAsync"/> で一覧に戻しておく)。
    /// </returns>
    public bool DraftKeepApart(SiblingFolderGroup group)
    {
        if (!IsListMode || !IsReady)
            return false;

        var (entry, replaced) = ProgramsDraft.KeepApart(KeepSeparate.Select(r => r.Current), group);
        _editingKeep = replaced.Count == 0 ? null : KeepSeparate.FirstOrDefault(r => ReferenceEquals(r.Current, replaced[0]));
        FormHeading = _editingKeep is null ? "分けておく兄弟フォルダを追加" : "分けておく兄弟フォルダを編集"; // W-229 / W-228
        FormStation = entry.Match.Station;
        FormFolders = string.Join("\n", entry.Match.Folders);
        FormAlbum = "";
        FormNote = entry.Note ?? "";
        FormErrors = [];
        FormNotice = KeepDraftFromAttention; // W-226
        Mode = ProgramsPageMode.KeepForm;
        return true;
    }

    /// <summary>
    /// 要対応の「分けておく」(W-127)。programs.json を読み直し、<c>KeepSeparate</c> に組を足して(組にフォルダが加わったなら
    /// 記録した組を置き換えて)その場で保存する(<see cref="ProgramsFileWriter"/>。バックアップあり・読んだ後に変わっていたら書かない)。
    /// 再適用も設定リロードも行わない(<c>KeepSeparate</c> は取り込みの結果を変えない)。この画面は、次に出したときに読み直す。
    /// </summary>
    public async Task<KeepApartResult> KeepApartAsync(SiblingFolderGroup group)
    {
        if (HasUnsavedWork)
            return KeepApartResult.Failed(KeepApartBlocked); // W-138

        string? path;
        try
        {
            path = _locateProgramsFile?.Invoke();
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: programs.json の場所を決められませんでした(分けておく): {ex}");
            return KeepApartResult.Failed(KeepApartFailed(LocateFailed(ex.Message)));
        }
        // 要対応は programs.json を使う設定のときだけ兄弟フォルダを出すので、ここには来ないはず。
        if (string.IsNullOrWhiteSpace(path))
            return KeepApartResult.Failed(KeepApartFailed(_locateProgramsFile is null ? Unavailable : Unused));

        var name = Path.GetFileName(path);
        try
        {
            var snapshot = await Task.Run(() => ProgramsFileWriter.Read(path));
            // コメントはファイル全体の書き直しで消える(.bak には残る)。黙って消さず、番組ページの下書きへ回して W-260 で確かめる。
            if (snapshot.CommentCount > 0)
                return KeepApartResult.NeedsDraft;

            var config = snapshot.Config;
            var (entry, replaced) = ProgramsDraft.KeepApart(config.KeepSeparate, group);
            var index = replaced.Count == 0 ? config.KeepSeparate.Count : config.KeepSeparate.IndexOf(replaced[0]);
            config.KeepSeparate.RemoveAll(k => replaced.Contains(k));
            config.KeepSeparate.Insert(Math.Min(index, config.KeepSeparate.Count), entry);

            var backup = await Task.Run(() => ProgramsFileWriter.Write(snapshot, config, _now()));
            _logInfo($"管理画面: {path} に分けておく組を書きました({entry.Match.Station}: {string.Join(" / ", entry.Match.Folders)}。"
                     + $"バックアップ: {backup ?? "なし(新しく作った)"})");
            return KeepApartResult.Written;
        }
        catch (ProgramsFileChangedException ex)
        {
            _logWarn($"管理画面: {path} に分けておく組を書きませんでした: {ex.Message}");
            return KeepApartResult.Failed(KeepApartFailed($"{name} は読み込んだ後に変更されています(エディタで直した等)。")); // W-275
        }
        catch (ProgramsFileException ex)
        {
            _logWarn($"管理画面: {path} を読めませんでした(分けておく): {ex}");
            return KeepApartResult.Failed(KeepApartFailed(ex.Message));
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: {path} に分けておく組を書けませんでした: {ex}");
            return KeepApartResult.Failed(KeepApartFailed($"書き込みに失敗しました: {ex.Message}(元のままです)")); // W-277
        }
    }

    /// <summary>W-139。「分けておく」で programs.json を書けなかった。</summary>
    public static string KeepApartFailed(string reason) => $"分けておく組を書けませんでした: {reason}";

    // W-124 の組が入っている統合番組のエントリ(下書きの一覧から。局が同じで、組のフォルダを含み、アルバム名が提案と同じもの)。
    private ProgramEntryRow? FindMergedRow(SiblingProgramProposal proposal, SiblingFolderGroup group)
    {
        var stationKey = TextNormalizer.ToMatchKey(group.Station);
        var folderKeys = group.Folders.Select(TextNormalizer.ToMatchKey).ToHashSet();
        return Programs.FirstOrDefault(row =>
            TextNormalizer.ToMatchKey(row.Station) == stationKey
            && row.Album == proposal.Album
            && row.Current.Match.Folders.Any(f => folderKeys.Contains(TextNormalizer.ToMatchKey(f))));
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
        IsSavedBasis = false;
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
        // ファイル名の番組名の判断(ADR-0021)はまだこのページで編集しない(#235)。読んだときのものをそのまま書き戻す(落とさない)。
        FilenamePrograms = snapshot.Config.FilenamePrograms,
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

    // ---- 保存済みの設定での確認(#164。設定ページの stations.json の「再読み込み」・要対応の「再適用の確認へ」)----

    /// <summary>
    /// 保存済みの設定で再適用を確かめる(W-252 / W-253)。設定リロード → dry-run(<see cref="IServiceControl.PlanReapplyAsync()"/>)を
    /// 行い、揃える回があれば確認の画面(W-312 の <c>適用</c> / <c>戻る</c>)にする。呼び出し元はそのとき番組ページへ移る
    /// (<see cref="SavedReapplyStart.ShowsConfirm"/>)。揃える回が無い・確かめられなかったときは、番組ページへ移らずに、来たページに
    /// 出す 1 行を返す(W-254 / W-255)。「戻る」は <paramref name="origin"/> へ戻る。
    /// <para>
    /// 番組ページに保存していない作業があれば始めない(W-256。確認の画面に替わると下書きが見えなくなるため。下書きは消さない)。
    /// 確かめている間は番組ページを「確かめている」画面(W-240)にし、編集を始められないようにする。
    /// </para>
    /// </summary>
    public async Task<SavedReapplyStart> ConfirmSavedAsync(ManagementPage origin)
    {
        if (SavedConfirmBlockedReason is { } blocked)
            return SavedReapplyStart.Stay(blocked, isWarning: true);
        if (_control is null)
            return SavedReapplyStart.Stay(SavedAborted(ConfirmUnavailable), isWarning: true);

        // まだ読んでいなければ先に読む。読んでいる途中(番組ページを開いた直後等)なら、終わるのを待つ。後から終わった読み込みの
        // SetSnapshot が、確認の画面を一覧に戻さないように(読み込みは LoadAsync が 1 本にまとめる)。
        if (!_loaded || _loading is { IsCompleted: false })
        {
            await LoadAsync();
            if (SavedConfirmBlockedReason is { } blockedAfterLoad)
                return SavedReapplyStart.Stay(blockedAfterLoad, isWarning: true);
        }

        _plan = null;
        _planDraft = null;
        _savedOrigin = origin;
        IsSavedBasis = true;
        IsCommentsPromptVisible = false;
        IsConflict = false;
        CheckingText = "";
        Mode = ProgramsPageMode.Checking;

        ReapplyResult planned;
        try
        {
            var planTask = _control.PlanReapplyAsync();
            if (await Task.WhenAny(planTask, Task.Delay(CheckingDelay)) != planTask)
                CheckingText = Checking; // W-240
            planned = await planTask;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 再適用の確認(保存済みの設定)に失敗しました: {ex}");
            planned = ReapplyResult.Failed($"再適用の確認に失敗しました: {ex.Message}");
        }

        if (planned.Report is not { } plan)
        {
            EndSavedConfirm();
            return SavedReapplyStart.Stay(SavedAborted(planned.Error ?? "理由不明"), isWarning: true); // W-255
        }
        if (plan.IsAligned)
        {
            EndSavedConfirm();
            return SavedReapplyStart.Stay(SavedAligned(plan), isWarning: false); // W-254
        }

        _plan = plan;
        DiffBefore = "";
        DiffAfter = "";
        var text = ReapplyDialogFormatter.BuildConfirmation(plan, ReapplyBasis.Saved); // W-252 / W-253
        ConfirmHeading = text.Heading;
        ConfirmBody = text.Body;
        SaveOnlyNote = null;
        Mode = ProgramsPageMode.Confirm;
        return SavedReapplyStart.Confirm;
    }

    /// <summary>W-254。保存済みの設定で確かめたら、揃える回が無かった(来たページに出す 1 行)。</summary>
    public static string SavedAligned(ReapplyReport plan)
    {
        var heading = ReapplyDialogFormatter.BuildAligned(plan, ReapplyBasis.Saved).Heading;
        var excluded = plan.Count(ReapplyAction.Excluded);
        return excluded > 0 ? $"{heading}(対象外の回が {excluded} 回あります。動かしません)" : heading;
    }

    /// <summary>W-255。保存済みの設定で確かめられなかった(来たページに出す 1 行。W-251 と同じ言い方)。</summary>
    public static string SavedAborted(string reason) => $"{Aborted}: {reason}";

    // W-312 の「適用」。確かめたときの設定で実行する(worker が確かめたときの設定・予定と同じかを確かめ、違えば実行しない。W-278)。
    private async Task ApplySavedAsync()
    {
        if (!IsConfirmMode || !IsSavedBasis || _plan is not { } plan || _control is null)
            return;

        _plan = null; // 同じ確認で 2 回実行しない(worker も直前の確認を忘れる)
        SavedMessage = null;
        Mode = ProgramsPageMode.Applying;
        ReapplyResult applied;
        try
        {
            applied = await _control.ApplyReapplyAsync(plan);
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 再適用の実行(保存済みの設定)に失敗しました: {ex}");
            applied = ReapplyResult.Failed($"再適用に失敗しました: {ex.Message}");
        }

        if (applied.IsStale)
            ShowResult(SavedStale, "", isError: true); // W-278
        else if (applied.Report is { } result)
        {
            // 保存したのに実行できなかった再適用も、保存済みの設定で実行できたので済んだ(変更が無いのに「確認」を押せる状態を下ろす)。
            _reapplyPending = false;
            var text = ReapplyDialogFormatter.BuildCompletion(result);
            ShowResult(text.Heading, text.Body, isError: result.Count(ReapplyAction.Failed) > 0);
        }
        else
            ShowResult(Aborted, applied.Error ?? "理由不明", isError: true); // W-251
    }

    // 保存済みの設定での確認を終え、番組ページを一覧に戻す(一覧の中身は、確かめる前のまま)。
    private void EndSavedConfirm()
    {
        _plan = null;
        IsSavedBasis = false;
        IsCommentsPromptVisible = false;
        IsConflict = false;
        Mode = ProgramsPageMode.List;
    }

    private async Task BackToListAsync()
    {
        if (IsSavedBasis)
        {
            // 保存済みの設定での確認・結果からは、来たページへ戻る(番組ページは一覧に戻しておく)。
            var origin = _savedOrigin;
            EndSavedConfirm();
            _navigate(origin);
            return;
        }

        await ReturnToListAsync();
    }

    /// <summary>
    /// 要対応から下書きを入れる前に呼ぶ(#199)。結果の画面(保存していない変更は無い)なら一覧に戻し、結果を出している間に
    /// ファイルが変わっていれば読み直す(「戻る」と同じ)。保存済みの設定での結果なら、確認を終えて一覧に戻す(来たページへは
    /// 移らない)。ほかの画面(編集フォーム・確認・適用中)と、保存していない変更が残ったままの結果の画面(読み込んだ後の変更
    /// W-275・書き込みの失敗 W-277 等で保存できなかった後)はそのままにする
    /// (下書きは作らず、番組ページの今の画面を見せる。ホームの「分けておく」は W-138 で押せない)。
    /// </summary>
    public async Task LeaveResultForDraftAsync()
    {
        if (!IsResultMode || HasChanges)
            return;
        if (IsSavedBasis)
        {
            EndSavedConfirm();
            if (await FileChangedAsync())
                await LoadAsync(); // 確かめている間に編集を始めていたら、LoadAsync が読み直さない
            return;
        }
        await ReturnToListAsync();
    }

    // 下書きの確認・結果の画面から一覧へ戻す。結果の画面を出している間に、ファイルが変わっていれば読み直す(OnShownAsync と
    // 同じ。保存していない変更があれば捨てないので読み直さない。W-275 の後の「戻る」がこれ)。
    private async Task ReturnToListAsync()
    {
        var fromResult = IsResultMode;
        IsCommentsPromptVisible = false;
        IsConflict = false;
        Mode = ProgramsPageMode.List;

        // ファイルが変わったかを確かめている間・読んでいる間(await の間)に、利用者が一覧を編集した・フォームや「確認」を
        // 始めていたら、読み直すとその作業が黙って消える。LoadAsync が読む前と読み終えた後に確かめ、そのときは作り直さない。
        if (fromResult && !HasChanges && await FileChangedAsync())
            await LoadAsync();
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

        // 設定リロード → 再適用の実行(ADR-0016)。worker の 1 つのジョブの中で続けて行う。リロードに失敗すれば再適用は
        // 行われず、その理由が返る(W-251)。書かなかった(保存済みの設定で実行し直す)ときもリロードを試し直す。
        Mode = ProgramsPageMode.Applying;
        ReapplyResult applied;
        try
        {
            applied = await _control!.ReloadAndApplyReapplyAsync(plan);
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 再適用の実行に失敗しました: {ex}");
            applied = ReapplyResult.Failed($"再適用に失敗しました: {ex.Message}");
        }

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
        await LoadAsync(discardWork: true); // W-276:この画面での変更を捨てると決めて読み直す
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
        ApplySavedCommand.NotifyCanExecuteChanged();
    }

    // W-210
    private static string CountText(int count) => count == 0 ? "まだありません" : $"{count} 件";
}
