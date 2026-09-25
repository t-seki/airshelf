using CommunityToolkit.Mvvm.ComponentModel;
using Radirec.Hosting.Control;

namespace Radirec.Gui.Core.Management;

/// <summary>管理画面のページ(W-002。この順に並べる)。</summary>
public enum ManagementPage
{
    Home,
    History,
    Programs,
    Settings,
    About,
}

/// <summary>
/// 管理画面(requirements §10.3・#151)の ViewModel。5 ページの枠と、いま出しているページを持つ。
/// 設定 / バージョン情報のページは、まだ中身が無い(#152・#157 が足す)。
/// <para>
/// 管理画面を開くたびに作り直す(開いた時点の状態・取込履歴から始める)。閉じても常駐は続く。
/// </para>
/// </summary>
public sealed class ManagementViewModel : ObservableObject
{
    /// <summary>W-001(ウィンドウの表題)。</summary>
    public const string Title = "Radirec";

    /// <summary>W-003(ウィンドウ下端の注記)。</summary>
    public const string CloseNote = "閉じてもトレイに常駐し、取り込みは続きます";

    /// <summary>W-002(ページの切り替え。<see cref="ManagementPage"/> の順)。</summary>
    public static IReadOnlyList<string> PageNames { get; } = ["ホーム", "取込履歴", "番組", "設定", "バージョン情報"];
    private ManagementPage _currentPage = ManagementPage.Home;
    private readonly Action<string> _logWarn;

    /// <param name="control">取り込みワーカーの制御面。縮退(ワーカーが無い)なら null。</param>
    /// <param name="degradedReason">control が null の理由(W-104 / W-105)。</param>
    /// <param name="loadHistory">台帳の全件を新しい順で返す。設定を読めず台帳の場所が決まらないなら null(W-150)。</param>
    /// <param name="pathActions">取込履歴の行をエクスプローラーで開く / コピーする。</param>
    /// <param name="logWarn">gui.log への記録。</param>
    /// <param name="zone">時刻を表示する時間帯(既定はローカル。テスト用)。</param>
    /// <param name="locateProgramsFile">
    /// 番組ページ(#154)の programs.json の場所。programs.json を使わない設定なら null を返す。設定を読めていない
    /// (場所が決まらない)なら、この引数自体を null にする(W-211)。
    /// </param>
    /// <param name="logInfo">gui.log への記録(保存したこと等)。</param>
    /// <param name="now">番組ページの保存でバックアップの名前に使う時刻(テスト用)。</param>
    public ManagementViewModel(
        IServiceControl? control,
        string? degradedReason,
        Func<IReadOnlyList<IngestHistoryRow>>? loadHistory,
        IHistoryPathActions pathActions,
        Action<string> logWarn,
        TimeZoneInfo? zone = null,
        Func<string?>? locateProgramsFile = null,
        Action<string>? logInfo = null,
        Func<DateTime>? now = null)
    {
        _logWarn = logWarn;
        var applyGate = new ProgramsApplyGate();
        Home = new HomeViewModel(control, degradedReason, loadHistory, logWarn,
            showAll: () => CurrentPage = ManagementPage.History, zone, applyGate);
        History = new IngestHistoryViewModel(loadHistory, pathActions, logWarn);
        Programs = new ProgramsPageViewModel(control, locateProgramsFile, applyGate, logWarn, logInfo, now);
    }

    public HomeViewModel Home { get; }

    public IngestHistoryViewModel History { get; }

    /// <summary>番組ページ(#154)。</summary>
    public ProgramsPageViewModel Programs { get; }

    /// <summary>いま出しているページ。</summary>
    public ManagementPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (!SetProperty(ref _currentPage, value))
                return;
            OnPropertyChanged(nameof(CurrentPageIndex));
            OnPropertyChanged(nameof(IsHomePage));
            OnPropertyChanged(nameof(IsHistoryPage));
            OnPropertyChanged(nameof(IsProgramsPage));
            OnPropertyChanged(nameof(IsSettingsPage));
            OnPropertyChanged(nameof(IsAboutPage));
            if (value == ManagementPage.Programs)
                _ = ShowProgramsAsync();
        }
    }

    /// <summary>ページの切り替え(左の一覧の選択)に結ぶ番号。範囲外(選択が外れた等)は無視する。</summary>
    public int CurrentPageIndex
    {
        get => (int)CurrentPage;
        set
        {
            if (Enum.IsDefined((ManagementPage)value))
                CurrentPage = (ManagementPage)value;
            else
                OnPropertyChanged(); // 一覧の選択を今のページへ戻す
        }
    }

    public bool IsHomePage => CurrentPage == ManagementPage.Home;
    public bool IsHistoryPage => CurrentPage == ManagementPage.History;
    public bool IsProgramsPage => CurrentPage == ManagementPage.Programs;
    public bool IsSettingsPage => CurrentPage == ManagementPage.Settings;
    public bool IsAboutPage => CurrentPage == ManagementPage.About;

    /// <summary>
    /// 管理画面が開いている間、シェルのタイマーが 2 秒ごとに呼ぶ。ホームの状態と、番組ページの「確認」を押せるか
    /// (取り込みが動いているか)を読み直す(取込履歴ページの全件は「更新」でだけ読み直すので、ここでは触らない)。
    /// </summary>
    public void Tick()
    {
        Home.Refresh();
        Programs.RefreshStatus();
    }

    // 番組ページを出したら programs.json を読む(まだ読んでいない・変更が無いときだけ)。例外は gui.log に書いて握る
    // (読み込みの失敗はページの中に出す。ここから漏らすと UI スレッドの最後の網が管理画面を閉じる)。
    private async Task ShowProgramsAsync()
    {
        try
        {
            await Programs.OnShownAsync();
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 番組ページを出せませんでした: {ex}");
        }
    }
}
