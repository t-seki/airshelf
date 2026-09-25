using CommunityToolkit.Mvvm.ComponentModel;
using Radirec.Core.Attention;
using Radirec.Core.Normalization;
using Radirec.Gui.Core.Updates;
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
/// 各ページの ViewModel:ホームと取込履歴は #151、番組ページは #154、設定ページは #152、バージョン情報ページは #157。
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
    /// <param name="about">バージョン情報ページ(#157)。省略すると、このアセンブリの版で、更新の項目の無いページ(W-510)にする。</param>
    /// <param name="settings">設定ページ(#152)。null なら見出しだけ。</param>
    /// <param name="computeAttention">
    /// ホームの要対応(#155・ADR-0018)を、台帳とディスクの設定ファイルから計算する。設定を読めない(縮退)なら null(W-137)。
    /// </param>
    public ManagementViewModel(
        IServiceControl? control,
        string? degradedReason,
        Func<IReadOnlyList<IngestHistoryRow>>? loadHistory,
        IHistoryPathActions pathActions,
        Action<string> logWarn,
        TimeZoneInfo? zone = null,
        Func<string?>? locateProgramsFile = null,
        Action<string>? logInfo = null,
        Func<DateTime>? now = null,
        AboutPageViewModel? about = null,
        SettingsPageViewModel? settings = null,
        Func<AttentionItems>? computeAttention = null)
    {
        _logWarn = logWarn;
        var applyGate = new ProgramsApplyGate();
        // 番組ページを先に作る(ホームの要対応の「統合する」「分けておく」が使う)。
        Programs = new ProgramsPageViewModel(control, locateProgramsFile, applyGate, logWarn, logInfo, now,
            navigate: page => CurrentPage = page);
        Home = new HomeViewModel(control, degradedReason, loadHistory, logWarn,
            showAll: () => CurrentPage = ManagementPage.History, zone, applyGate,
            computeAttention is null ? null : new AttentionActions(this, computeAttention),
            pathActions,
            savedReapply: SavedReapplyEntry(ManagementPage.Home)); // 「再適用の確認へ」(W-136。#156)
        History = new IngestHistoryViewModel(loadHistory, pathActions, logWarn);
        About = about ?? new AboutPageViewModel(
            AppVersion.Of(typeof(ManagementViewModel).Assembly), UpdateSupport.NotInstalled,
            updater: null, checker: null, _ => false, logWarn);
        Settings = settings;
        // 設定ページの stations.json の「再読み込み」は、番組ページの確認の画面で再適用を確かめる(#164)。
        if (settings is not null)
            settings.SavedReapply = SavedReapplyEntry(ManagementPage.Settings);
    }

    /// <summary>
    /// 保存済みの設定で再適用を確かめる入口(#164)。<paramref name="origin"/> は来たページで、確認の画面の「戻る」でそこへ戻る。
    /// 設定ページの stations.json の「再読み込み」と、ホームの要対応の「再適用の確認へ」(W-136。#156)が使う。
    /// </summary>
    public ISavedReapplyEntry SavedReapplyEntry(ManagementPage origin) => new SavedReapply(this, origin);

    public HomeViewModel Home { get; }

    public IngestHistoryViewModel History { get; }

    /// <summary>番組ページ(#154)。</summary>
    public ProgramsPageViewModel Programs { get; }

    /// <summary>設定ページ(#152)。</summary>
    public SettingsPageViewModel? Settings { get; }

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
            if (value == ManagementPage.Programs)
                _ = ShowProgramsAsync();
            // 要対応は、番組ページで保存した後などにホームへ戻ったら計算し直す(2 秒ごとには計算しない。#155)。
            if (value == ManagementPage.Home)
                Home.RefreshAttention();
            // 設定ページは開くたびにファイルの中身と開く場所を読み直す(読み込みの失敗は中で握る)。
            if (value == ManagementPage.Settings && Settings is { } settings)
                _ = settings.RefreshAsync();
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
    /// バージョン情報ページは保留中の更新を読み直す(#157)。ホームの「再適用の確認へ」(W-136)を押せるかも、ホームの
    /// <see cref="HomeViewModel.Refresh"/> が読み直す(番組ページに保存していない変更があれば押せない。W-256)。
    /// </summary>
    public void Tick()
    {
        Home.Refresh();
        Programs.RefreshStatus();
        About.Refresh();
        // stations.json の「再読み込み」を押せるか(番組ページに保存していない変更があれば押せない。W-256)。
        Settings?.RefreshSavedReapply();
    }

    // 保存済みの設定での再適用の確認(#164)。揃える回があれば番組ページの確認の画面へ移る。例外は gui.log に書いて握る
    // (呼び出し元はボタンのハンドラ。漏らすと UI スレッドの最後の網が管理画面を閉じる)。
    private sealed class SavedReapply(ManagementViewModel owner, ManagementPage origin) : ISavedReapplyEntry
    {
        public string? BlockedReason => owner.Programs.SavedConfirmBlockedReason;

        public async Task<SavedReapplyStart> StartAsync()
        {
            SavedReapplyStart start;
            try
            {
                start = await owner.Programs.ConfirmSavedAsync(origin);
            }
            catch (Exception ex)
            {
                owner._logWarn($"管理画面: 再適用の確認(保存済みの設定)を始められませんでした: {ex}");
                return SavedReapplyStart.Stay(ProgramsPageViewModel.SavedAborted($"{ex.Message}(gui.log 参照)"), isWarning: true);
            }
            if (start.ShowsConfirm)
                owner.CurrentPage = ManagementPage.Programs;
            return start;
        }
    }

    // 要対応の「統合する」「分けておく」(#155)。下書きは番組ページに作り、そのページへ移る。
    private sealed class AttentionActions(ManagementViewModel owner, Func<AttentionItems> compute) : IAttentionActions
    {
        public AttentionItems Compute() => compute();

        public string? KeepApartBlockedReason =>
            owner.Programs.HasUnsavedWork ? ProgramsPageViewModel.KeepApartBlocked : null; // W-138

        public void Integrate(SiblingFolderGroup group)
            => _ = owner.ShowDraftAsync(programs => programs.DraftIntegration(group));

        public async Task<string?> KeepApartAsync(SiblingFolderGroup group)
        {
            var result = await owner.Programs.KeepApartAsync(group);
            if (result.Outcome == KeepApartOutcome.NeedsDraft)
            {
                await owner.ShowDraftAsync(programs => programs.DraftKeepApart(group));
                return null;
            }
            return result.Message;
        }
    }

    // 番組ページを読み込んでから下書きを入れ、そのページへ移る(ページを出すときの読み込みは、下書きがあれば読み直さない)。
    // 番組ページが結果の画面のまま(保存した後・保存済みの設定で適用した後)なら、一覧に戻してから入れる(#199)。
    // 下書きを作れなかった(読めない・編集の途中等)ときも移り、番組ページの状態をそのまま見せる。
    private async Task ShowDraftAsync(Func<ProgramsPageViewModel, bool> draft)
    {
        try
        {
            await Programs.OnShownAsync();
            await Programs.LeaveResultForDraftAsync();
            draft(Programs);
        }
        catch (Exception ex)
        {
            _logWarn($"管理画面: 要対応から番組ページの下書きを作れませんでした: {ex}");
        }
        CurrentPage = ManagementPage.Programs;
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
