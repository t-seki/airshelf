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
/// 番組 / 設定のページは、まだ中身が無い(#152・#154 が足す)。
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

    /// <param name="control">取り込みワーカーの制御面。縮退(ワーカーが無い)なら null。</param>
    /// <param name="degradedReason">control が null の理由(W-104 / W-105)。</param>
    /// <param name="loadHistory">台帳の全件を新しい順で返す。設定を読めず台帳の場所が決まらないなら null(W-150)。</param>
    /// <param name="pathActions">取込履歴の行をエクスプローラーで開く / コピーする。</param>
    /// <param name="logWarn">gui.log への記録。</param>
    /// <param name="zone">時刻を表示する時間帯(既定はローカル。テスト用)。</param>
    /// <param name="about">バージョン情報ページ(#157)。省略すると、このアセンブリの版で更新の項目の無いページにする。</param>
    public ManagementViewModel(
        IServiceControl? control,
        string? degradedReason,
        Func<IReadOnlyList<IngestHistoryRow>>? loadHistory,
        IHistoryPathActions pathActions,
        Action<string> logWarn,
        TimeZoneInfo? zone = null,
        AboutPageViewModel? about = null)
    {
        Home = new HomeViewModel(control, degradedReason, loadHistory, logWarn,
            showAll: () => CurrentPage = ManagementPage.History, zone);
        History = new IngestHistoryViewModel(loadHistory, pathActions, logWarn);
        About = about ?? new AboutPageViewModel(
            AppVersion.Of(typeof(ManagementViewModel).Assembly), updater: null, checker: null, _ => false, logWarn);
    }

    public HomeViewModel Home { get; }

    public IngestHistoryViewModel History { get; }

    public AboutPageViewModel About { get; }

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
    /// 管理画面が開いている間、シェルのタイマーが 2 秒ごとに呼ぶ。ホームの状態を読み直す(取込履歴ページの全件は
    /// 「更新」でだけ読み直すので、ここでは触らない)。バージョン情報ページは保留中の更新を読み直す(#157)。
    /// </summary>
    public void Tick()
    {
        Home.Refresh();
        About.Refresh();
    }
}
