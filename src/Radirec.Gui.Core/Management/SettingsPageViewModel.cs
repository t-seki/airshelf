using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Radirec.Hosting;
using Radirec.Hosting.Control;

namespace Radirec.Gui.Core.Management;

/// <summary>設定ファイルを開いた結果(W-308)。<see cref="Message"/> は画面に出す文言(知らせることが無ければ null)。</summary>
public sealed record SettingsFileOpenResult(bool Ok, string? Message);

/// <summary>
/// 設定ページがシェル(Windows)に頼むこと。WPF / シェル実行 / 再起動を ViewModel から切り離し、WSL の CI でテストできるようにする。
/// </summary>
public interface ISettingsPageActions
{
    /// <summary>設定ファイルを関連付けたエディタで開く。無ければ見本から作ってから開く。</summary>
    Task<SettingsFileOpenResult> OpenAsync(ConfigFile file, string path);

    /// <summary>ファイルの中身を読む。ファイルが無ければ null、読めなければ投げる。ワーカースレッドから呼ばれる。</summary>
    string? ReadText(string path);

    /// <summary>設定リロードの確認(W-309)。既定はキャンセル。OK なら true。</summary>
    bool ConfirmReload(string message);

    /// <summary>プロセスごと再起動する(W-404 / W-409「再読み込みして再起動」)。</summary>
    void Restart();
}

/// <summary>
/// 設定ページの 1 つの設定ファイルの段(radirec.json / stations.json)。programs.json は中身を出さず、起動の失敗のパネル(W-409)で
/// 開く場所にだけ使う。
/// </summary>
public sealed class SettingsFileRow : ObservableObject
{
    private string? _path;
    private string? _content;
    private string? _note;
    private string? _message;
    private bool _isMessageWarning;

    internal SettingsFileRow(ConfigFile file, string name)
    {
        File = file;
        Name = name;
    }

    public ConfigFile File { get; }

    /// <summary>見出しのファイル名(既定の名前)。</summary>
    public string Name { get; }

    /// <summary>開く場所。場所が決まらない(そのファイルを使わない設定)なら null。</summary>
    public string? Path
    {
        get => _path;
        internal set
        {
            if (SetProperty(ref _path, value))
                OnPropertyChanged(nameof(HasPath));
        }
    }

    public bool HasPath => Path is not null;

    /// <summary>読み取り専用で見せる中身(秘密は伏せてある)。読めていなければ null。</summary>
    public string? Content { get => _content; internal set => SetProperty(ref _content, value); }

    /// <summary>中身の代わりに出す 1 行(W-307 / W-315 / W-317)。</summary>
    public string? Note { get => _note; internal set => SetProperty(ref _note, value); }

    /// <summary>操作の結果(W-308 / W-310)。</summary>
    public string? Message { get => _message; internal set => SetProperty(ref _message, value); }

    /// <summary><see cref="Message"/> が失敗・拒否か(警告の色で出す)。</summary>
    public bool IsMessageWarning { get => _isMessageWarning; internal set => SetProperty(ref _isMessageWarning, value); }
}

/// <summary>
/// 管理画面の設定ページ(requirements §10.3・#152)。
/// <list type="bullet">
///   <item>新規取込のトーストの切り替え(W-301〜W-303)。値は管理画面用の設定ファイルに持つ(ADR-0019)</item>
///   <item>radirec.json・stations.json を読み取り専用で見せ、「ファイルを開く」を付ける(W-304〜W-308)。
///     radirec.json には「再読み込み」(設定リロード。W-309 / W-310)も付ける。stations.json の「再読み込み」は、設定リロードの後に
///     保存済みの設定で再適用を確かめ、揃える回があれば番組ページの確認の画面へ進む(W-311・W-312。#164)</item>
///   <item>radirec.json を読めない(縮退)ときは、代わりに縮退パネル(W-401〜W-405)</item>
///   <item>radirec.json は読めたが取り込みを起動できなかった(W-104)ときは、ページの先頭に起動の失敗のパネル(W-406〜W-409。#168)。
///     原因になりうる programs.json・stations.json を開いて直し、「再読み込みして再起動」で戻れるようにする</item>
/// </list>
/// 中身はページを開くたびに読み直す(<see cref="RefreshAsync"/>)。ファイルを保存しただけでは取り込みに反映しないので、
/// ここに見えているのは「ディスクにある今の中身」で、反映には「再読み込み」が要る(W-304)。
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    /// <summary>W-301。</summary>
    public const string ToastHeading = "取り込みの知らせ";

    /// <summary>W-302。</summary>
    public const string ToastLabel = "新規取込のトーストを出す";

    /// <summary>W-303。</summary>
    public const string ToastNote = "取込失敗のトーストは、この設定に関係なく出ます。";

    /// <summary>W-314。切り替えを保存できなかったとき(チェックは元に戻す)。</summary>
    public const string ToastSaveFailed = "設定を保存できませんでした(gui.log 参照)";

    /// <summary>W-304(見出し)。</summary>
    public const string FilesHeading = "設定ファイル";

    /// <summary>W-304(本文)。</summary>
    public const string FilesNote =
        "読み取り専用です。直すときは「ファイルを開く」でエディタで開き、保存してから「再読み込み」を押してください。ファイルを保存しただけでは反映しません。";

    /// <summary>W-306。<c>JellyfinApiKey</c>・<c>UpdateToken</c> が空でないとき、値の代わりに出す。</summary>
    public const string SecretHidden = "(設定済み・表示しません)";

    /// <summary>W-307。</summary>
    public const string FileNotUsed = "radirec.json でこのファイルを使わない設定になっています";

    /// <summary>W-309。</summary>
    public const string ReloadConfirm = "設定を再読み込みします。取り込みは一時停止し、再構築されます。よろしいですか?";

    /// <summary>W-310(受理)。</summary>
    public const string ReloadAccepted = "設定リロードを受理しました(反映は取込完了後)";

    /// <summary>W-313。</summary>
    public const string ProgramsNoteUsed = "programs.json は「番組」ページで編集します";

    /// <summary>W-212(設定ページでは W-313 の代わりに出す)。</summary>
    public const string ProgramsNoteUnused = "radirec.json で programs.json を使わない設定になっています";

    /// <summary>W-316。取り込みが止まっていて、設定リロードを受け付けられないときの「再読み込み」の横。</summary>
    public const string ReloadUnavailable = "取り込みが止まっているため、再読み込みできません";

    /// <summary>W-311。stations.json の「再読み込み」を押してから、確かめ終わるまで(stations.json の段に出す)。</summary>
    public const string StationsReloadChecking = "stations.json を読み込み直し、取り込み済みの回を今の設定と比べます";

    /// <summary>W-401。</summary>
    public const string DegradedHeading = "設定を読めません";

    /// <summary>W-403。</summary>
    public const string DegradedNote = "取り込みは止まっています。ファイルを直して保存してから「再読み込みして再起動」を押してください。";

    /// <summary>W-405。</summary>
    public const string DegradedOtherFiles = "設定を読み込めていないため、場所が分かりません";

    /// <summary>W-406。</summary>
    public const string StartFailedHeading = "取り込みを起動できません";

    /// <summary>W-408。</summary>
    public const string StartFailedNote =
        "取り込みは止まっています。原因の設定ファイル(programs.json・stations.json など)を直して保存してから「再読み込みして再起動」を押してください。";

    // 伏せる秘密のキー(radirec.json)。値が空でない文字列のときだけ伏せる(空なら「未設定」と分かるようにそのまま)。
    [GeneratedRegex(@"(""(?:JellyfinApiKey|UpdateToken)""\s*:\s*)""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    private readonly IServiceControl? _control;
    private readonly string? _configPath;
    private readonly RadirecConfig? _startupConfig;
    private readonly IngestToastSetting _toastSetting;
    private readonly ISettingsPageActions _actions;
    private readonly Action<string> _logWarn;

    private string? _toastMessage;
    private bool _canReload;
    private string? _programsNote;
    private ISavedReapplyEntry? _savedReapply;
    private bool _stationsChecking; // stations.json の「再読み込み」で確かめている途中

    /// <param name="control">取り込みワーカーの制御面。縮退(ワーカーが無い)なら null。</param>
    /// <param name="configPath">radirec.json の絶対パス(読めなかったときも渡す。直すために開く)。</param>
    /// <param name="startupConfig">起動時に読めた設定。読めなかった(縮退パネルを出す)なら null。</param>
    /// <param name="configError">radirec.json を読めなかった理由(W-402)。読めたなら null。</param>
    /// <param name="toastSetting">新規取込のトーストの設定(W-302)。</param>
    /// <param name="actions">シェルに頼むこと(開く・読む・確認・再起動)。</param>
    /// <param name="logWarn">gui.log への記録。</param>
    /// <param name="startError">
    /// radirec.json は読めたが、取り込みワーカーを起動できなかった(W-104)ときの例外のメッセージ(W-407)。起動できたなら null。
    /// </param>
    public SettingsPageViewModel(
        IServiceControl? control,
        string? configPath,
        RadirecConfig? startupConfig,
        string? configError,
        IngestToastSetting toastSetting,
        ISettingsPageActions actions,
        Action<string> logWarn,
        string? startError = null)
    {
        _control = control;
        _configPath = configPath;
        _startupConfig = startupConfig;
        _toastSetting = toastSetting;
        _actions = actions;
        _logWarn = logWarn;

        IsDegraded = startupConfig is null;
        DegradedReason = configError ?? "";
        ConfigPath = configPath ?? "";
        // 縮退(radirec.json を読めない)なら縮退パネルが代わりに出るので、起動の失敗のパネルは出さない。
        IsStartFailed = !IsDegraded && startError is not null;
        StartFailedReason = IsStartFailed ? startError! : "";

        Radirec = new SettingsFileRow(ConfigFile.Radirec, "radirec.json") { Path = configPath };
        Stations = new SettingsFileRow(ConfigFile.Stations, "stations.json");
        Programs = new SettingsFileRow(ConfigFile.Programs, "programs.json");

        OpenRadirecCommand = new AsyncRelayCommand(() => OpenAsync(Radirec), () => Radirec.HasPath);
        OpenStationsCommand = new AsyncRelayCommand(() => OpenAsync(Stations), () => Stations.HasPath);
        StartFailedPanel = new SettingsFileRow(ConfigFile.Radirec, "");
        OpenProgramsCommand = new AsyncRelayCommand(() => OpenAsync(Programs, StartFailedPanel), () => Programs.HasPath);
        OpenStationsFromPanelCommand = new AsyncRelayCommand(() => OpenAsync(Stations, StartFailedPanel), () => Stations.HasPath);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => CanReload);
        ReloadStationsCommand = new AsyncRelayCommand(ReloadStationsAsync, () => CanReloadStations);
        RestartCommand = new RelayCommand(Restart);

        UpdateLocations();
    }

    /// <summary>radirec.json を読めていない(縮退パネル W-401〜W-405 を出す)。</summary>
    public bool IsDegraded { get; }

    /// <summary>縮退していない(通常の設定ページを出す)。</summary>
    public bool IsNormal => !IsDegraded;

    /// <summary>W-402(読み込みの戻り値をそのまま)。</summary>
    public string DegradedReason { get; }

    /// <summary>radirec.json のフルパス(縮退パネルに出す)。</summary>
    public string ConfigPath { get; }

    /// <summary>radirec.json は読めたが、取り込みを起動できなかった(W-104)。ページの先頭に W-406〜W-409 のパネルを出す。</summary>
    public bool IsStartFailed { get; }

    /// <summary>W-407(起動に失敗した例外のメッセージをそのまま)。</summary>
    public string StartFailedReason { get; }

    public SettingsFileRow Radirec { get; }

    public SettingsFileRow Stations { get; }

    /// <summary>
    /// programs.json。中身は出さず(番組ページで編集する。W-313)、起動の失敗のパネルの「programs.json を開く」(W-409)にだけ使う。
    /// </summary>
    public SettingsFileRow Programs { get; }

    /// <summary>
    /// 起動の失敗のパネル(W-406〜W-409)の結果の 1 行(W-308)。<see cref="SettingsFileRow.Message"/> だけを使う。
    /// </summary>
    public SettingsFileRow StartFailedPanel { get; }

    /// <summary>W-313 / W-212。</summary>
    public string? ProgramsNote { get => _programsNote; private set => SetProperty(ref _programsNote, value); }

    /// <summary>「新規取込のトーストを出す」(W-302)。切り替えるとすぐ保存し、すぐ効く。</summary>
    public bool IngestToastsEnabled
    {
        get => _toastSetting.Enabled;
        set
        {
            if (value == _toastSetting.Enabled)
                return;
            try
            {
                _toastSetting.Set(value);
                ToastMessage = null;
            }
            catch (Exception ex)
            {
                _logWarn($"管理画面: 新規取込のトーストの設定を保存できませんでした: {ex}");
                ToastMessage = ToastSaveFailed;
            }
            // 失敗したら元の値のままなので、チェックを戻すためにも通知する。
            OnPropertyChanged();
        }
    }

    /// <summary>切り替えの結果(W-314)。何も出さないときは null。</summary>
    public string? ToastMessage { get => _toastMessage; private set => SetProperty(ref _toastMessage, value); }

    /// <summary>radirec.json の「再読み込み」(設定リロード)を押せるか。取り込みが止まっていれば押せない。</summary>
    public bool CanReload
    {
        get => _canReload;
        private set
        {
            if (SetProperty(ref _canReload, value))
            {
                OnPropertyChanged(nameof(ReloadNote));
                ReloadCommand.NotifyCanExecuteChanged();
                RefreshSavedReapply();
            }
        }
    }

    /// <summary>W-316。「再読み込み」を押せないときの説明。</summary>
    public string? ReloadNote => CanReload ? null : ReloadUnavailable;

    /// <summary>
    /// 保存済みの設定で再適用を確かめる入口(#164)。管理画面(<see cref="ManagementViewModel"/>)がつなぐ。つながっていなければ
    /// stations.json の「再読み込み」は押せない。
    /// </summary>
    public ISavedReapplyEntry? SavedReapply
    {
        get => _savedReapply;
        set
        {
            _savedReapply = value;
            RefreshSavedReapply();
        }
    }

    /// <summary>
    /// stations.json の「再読み込み」を押せるか。取り込みが止まっている(W-316)・stations.json を使わない設定・番組ページに保存して
    /// いない変更がある(W-256)・確かめている途中なら押せない。
    /// </summary>
    public bool CanReloadStations =>
        CanReload && Stations.HasPath && _savedReapply is { BlockedReason: null } && !_stationsChecking;

    /// <summary>W-316 / W-256。stations.json の「再読み込み」を押せない理由(stations.json の段に出す)。押せるなら null。</summary>
    public string? StationsReloadNote =>
        !Stations.HasPath ? null
        : !CanReload ? ReloadUnavailable
        : _savedReapply?.BlockedReason;

    /// <summary>
    /// stations.json の「再読み込み」を押せるかを計算し直す(番組ページで編集を始めた・やめた等に追従する)。管理画面のタイマーが
    /// 2 秒ごとに呼ぶ。
    /// </summary>
    public void RefreshSavedReapply()
    {
        OnPropertyChanged(nameof(CanReloadStations));
        OnPropertyChanged(nameof(StationsReloadNote));
        ReloadStationsCommand?.NotifyCanExecuteChanged();
    }

    public IAsyncRelayCommand OpenRadirecCommand { get; }
    public IAsyncRelayCommand OpenStationsCommand { get; }

    /// <summary>W-409「programs.json を開く」(起動の失敗のパネル)。</summary>
    public IAsyncRelayCommand OpenProgramsCommand { get; }

    /// <summary>W-409「stations.json を開く」(起動の失敗のパネル)。結果は下の stations.json の段ではなく、パネルの 1 行に出す。</summary>
    public IAsyncRelayCommand OpenStationsFromPanelCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }

    /// <summary>
    /// W-305 の stations.json の「再読み込み」。設定リロード → 保存済みの設定で再適用を確かめ(W-311)、揃える回があれば番組ページの
    /// 確認の画面へ移る(W-252 / W-253 / W-312)。無ければ移らずに、結果の 1 行(W-254 / W-255)を stations.json の段に出す。
    /// </summary>
    public IAsyncRelayCommand ReloadStationsCommand { get; }

    /// <summary>W-404 / W-409「再読み込みして再起動」(縮退パネル・起動の失敗のパネル)。</summary>
    public IRelayCommand RestartCommand { get; }

    /// <summary>
    /// ページを開いたときに呼ぶ。開く場所(設定リロードの反映に追従する)と「再読み込み」を押せるかを計算し直し、
    /// radirec.json・stations.json の中身を読み直す。ファイルの読み込みはワーカースレッドで行う(NAS を指していても
    /// UI スレッドを塞がない)。
    /// </summary>
    public async Task RefreshAsync()
    {
        UpdateLocations();
        if (IsDegraded)
            return;

        await LoadContentAsync(Radirec, mask: true);
        await LoadContentAsync(Stations, mask: false);
    }

    /// <summary>radirec.json の中身の秘密(<c>JellyfinApiKey</c>・<c>UpdateToken</c>)を W-306 に置き換える。</summary>
    public static string MaskSecrets(string json) =>
        SecretPattern().Replace(json, m => m.Groups[2].Value.Length == 0 ? m.Value : m.Groups[1].Value + SecretHidden);

    /// <summary>W-315。中身を読めなかったとき。</summary>
    public static string ReadFailed(string name) => $"{name} を読めませんでした(gui.log 参照)";

    /// <summary>W-317。ファイルがまだ無いとき(「ファイルを開く」で見本から作る)。</summary>
    public static string FileMissing(string name) => $"{name} はまだありません(「ファイルを開く」で見本から作ります)";

    /// <summary>W-310(拒否)。</summary>
    public static string ReloadRejected(string? error) => $"設定リロードは拒否されました: {error}";

    // 開く場所 = 今読み込んでいる設定から計算した場所。ワーカーがあれば状態(設定リロードの反映に追従する)、
    // 無ければ起動時に読めた設定から(#143 で作った開く場所の決め方を、設定ページの「ファイルを開く」が引き継ぐ。#152)。
    private void UpdateLocations()
    {
        ServiceStatusSnapshot? status = null;
        if (_control is not null)
        {
            try
            {
                status = _control.GetStatus();
            }
            catch (Exception ex)
            {
                _logWarn($"管理画面: 設定ページで状態を読めませんでした: {ex}");
            }
        }

        CanReload = status is not null && ServiceStatusText.From(status).CanControl;

        if (_configPath is null || IsDegraded)
        {
            Stations.Path = null;
            Programs.Path = null;
            ProgramsNote = null;
            RefreshSavedReapply();
            return;
        }

        var stationsFile = status?.StationsFile ?? _startupConfig?.StationsFile;
        var programsFile = status?.ProgramsFile ?? _startupConfig?.ProgramsFile;
        Stations.Path = ConfigPaths.Locate(ConfigFile.Stations, _configPath, stationsFile, programsFile);
        Programs.Path = ConfigPaths.Locate(ConfigFile.Programs, _configPath, stationsFile, programsFile);
        ProgramsNote = Programs.Path is null ? ProgramsNoteUnused : ProgramsNoteUsed;

        OpenStationsCommand.NotifyCanExecuteChanged();
        OpenProgramsCommand.NotifyCanExecuteChanged();
        OpenStationsFromPanelCommand.NotifyCanExecuteChanged();
        RefreshSavedReapply();
    }

    private async Task LoadContentAsync(SettingsFileRow row, bool mask)
    {
        if (row.Path is not { } path)
        {
            row.Content = null;
            row.Note = FileNotUsed; // W-307
            return;
        }

        try
        {
            var text = await Task.Run(() => _actions.ReadText(path));
            if (text is null)
            {
                row.Content = null;
                row.Note = FileMissing(System.IO.Path.GetFileName(path));
                return;
            }
            row.Content = mask ? MaskSecrets(text) : text;
            row.Note = null;
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: {path} を読めませんでした: {ex}");
            row.Content = null;
            row.Note = ReadFailed(System.IO.Path.GetFileName(path));
        }
    }

    // messageTo: 結果の 1 行(W-308)を出す先。省略すると開いたファイルの段。起動の失敗のパネルのボタンは、パネルの 1 行に出す
    // (stations.json の結果が、パネルと下の stations.json の段の 2 か所に出ないように)。
    private async Task OpenAsync(SettingsFileRow row, SettingsFileRow? messageTo = null)
    {
        if (row.Path is not { } path)
            return;

        var target = messageTo ?? row;
        var name = System.IO.Path.GetFileName(path);
        try
        {
            var result = await _actions.OpenAsync(row.File, path);
            SetMessage(target, result.Message, warning: !result.Ok);
            // 見本から作ったので中身が変わった(programs.json は中身を出さないので読まない)。
            if (result.Ok && result.Message is not null && !IsDegraded && row.File != ConfigFile.Programs)
                await LoadContentAsync(row, mask: row.File == ConfigFile.Radirec);
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: {path} を開くときに例外: {ex}");
            SetMessage(target, $"{name} を開けませんでした(gui.log 参照)", warning: true); // W-308
        }
    }

    private async Task ReloadAsync()
    {
        if (_control is null || !CanReload)
            return;
        if (!_actions.ConfirmReload(ReloadConfirm))
            return;

        ReloadResult result;
        try
        {
            // 読込 + 構文検証だけ同期で行い、受理 / 拒否がすぐ返る(実際の差し替えは在中スキャンの完了後)。
            result = _control.RequestReload();
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 設定リロードの要求で例外: {ex}");
            result = ReloadResult.Failed($"{ex.Message}(gui.log 参照)");
        }

        if (result.Accepted)
            SetMessage(Radirec, ReloadAccepted, warning: false);
        else
            SetMessage(Radirec, ReloadRejected(result.Error), warning: true);

        await RefreshAsync();
    }

    // stations.json の「再読み込み」(#164)。設定リロード → 保存済みの設定で再適用を確かめる。揃える回があれば番組ページの確認の
    // 画面へ移り(この段の 1 行は消す)、無ければ結果の 1 行をこの段に出す。W-309 の確認は出さない(次の確認の画面が確認を兼ねる)。
    private async Task ReloadStationsAsync()
    {
        if (!CanReloadStations || _savedReapply is not { } entry)
            return;

        _stationsChecking = true;
        RefreshSavedReapply();
        SetMessage(Stations, StationsReloadChecking, warning: false); // W-311
        SavedReapplyStart start;
        try
        {
            start = await entry.StartAsync();
        }
        catch (Exception ex)
        {
            // 入口は投げない約束だが、ボタンのハンドラから漏らさない。
            _logWarn($"管理画面: stations.json の再読み込みで例外: {ex}");
            start = SavedReapplyStart.Stay(ProgramsPageViewModel.SavedAborted($"{ex.Message}(gui.log 参照)"), isWarning: true);
        }
        finally
        {
            _stationsChecking = false;
        }

        SetMessage(Stations, start.Message, start.IsWarning); // W-254 / W-255 / W-256(移ったときは消す)
        RefreshSavedReapply();
        // 設定リロードで場所が変わりうるので、開く場所と中身を読み直す(番組ページへ移ったときは、戻ってきたときに読み直す)。
        if (!start.ShowsConfirm)
            await RefreshAsync();
    }

    private void Restart()
    {
        try
        {
            _actions.Restart();
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 再起動できませんでした: {ex}");
        }
    }

    private static void SetMessage(SettingsFileRow row, string? message, bool warning)
    {
        row.Message = message;
        row.IsMessageWarning = warning && message is not null;
    }
}
