using Radirec.Gui.Core.Management;
using Radirec.Gui.Core.Updates;

namespace Radirec.Tests;

// バージョン情報ページと更新の確認(#157)。文言の ID は docs/mockups/management-window.md。
public sealed class AboutPageViewModelTests
{
    private sealed class FakeUpdater : IAppUpdater
    {
        public bool IsInstalled { get; set; } = true;
        public string? PendingVersion { get; set; }

        /// <summary>次の確認で見つかる版(null なら最新)。</summary>
        public string? Available { get; set; }
        public Exception? ThrowOnCheck { get; set; }
        public Exception? ThrowOnApply { get; set; }
        public TaskCompletionSource? HoldCheck { get; set; }
        public int Checks { get; private set; }
        public int Applies { get; private set; }

        public async Task<bool> CheckAsync()
        {
            Checks++;
            if (HoldCheck is { } hold)
                await hold.Task;
            if (ThrowOnCheck is { } ex)
                throw ex;
            PendingVersion = Available;
            return PendingVersion is not null;
        }

        public Task ApplyAndRestartAsync()
        {
            Applies++;
            return ThrowOnApply is { } ex ? Task.FromException(ex) : Task.CompletedTask;
        }
    }

    private readonly List<string> _log = [];
    private readonly List<string> _confirms = [];

    private AboutPageViewModel Page(
        FakeUpdater? updater, bool confirm = true, UpdateChecker? checker = null,
        UpdateSupport support = UpdateSupport.Supported)
    {
        checker ??= updater is null ? null : new UpdateChecker(updater, _log.Add);
        return new AboutPageViewModel("0.12.0", support, updater, checker, message =>
        {
            _confirms.Add(message);
            return confirm;
        }, _log.Add);
    }

    // ---- UpdateChecker ----

    [Fact]
    public async Task Checker_AutoFindsUpdate_NotifiesOnlyOnce()
    {
        var updater = new FakeUpdater { Available = "0.13.0" };
        var checker = new UpdateChecker(updater, _log.Add);

        var first = await checker.CheckAsync(manual: false);
        var second = await checker.CheckAsync(manual: false);

        Assert.Equal(new UpdateCheckResult(UpdateCheckOutcome.Found, "0.13.0", NotifyBalloon: true), first);
        Assert.Equal(new UpdateCheckResult(UpdateCheckOutcome.Found, "0.13.0", NotifyBalloon: false), second);
    }

    [Fact]
    public async Task Checker_AfterNotifying_DoesNotNotifyForNewerVersionUntilUpToDate()
    {
        // 知らせたかどうかは版を区別しない(旧 UpdateCoordinator と同じ)。最新に戻るまでは、別の版でも知らせない。
        var updater = new FakeUpdater { Available = "0.13.0" };
        var checker = new UpdateChecker(updater, _log.Add);
        await checker.CheckAsync(manual: false);

        updater.Available = "0.14.0";
        var newer = await checker.CheckAsync(manual: false);

        Assert.Equal(new UpdateCheckResult(UpdateCheckOutcome.Found, "0.14.0", NotifyBalloon: false), newer);
    }

    [Fact]
    public async Task Checker_ManualFind_NeverBalloons_AndSuppressesLaterAutoBalloon()
    {
        var updater = new FakeUpdater { Available = "0.13.0" };
        var checker = new UpdateChecker(updater, _log.Add);

        var manual = await checker.CheckAsync(manual: true);
        var auto = await checker.CheckAsync(manual: false);

        Assert.False(manual.NotifyBalloon); // 結果は画面の中に出す
        Assert.False(auto.NotifyBalloon);   // 画面で見たので、バルーンを重ねない
    }

    [Fact]
    public async Task Checker_AfterUpToDate_NotifiesAgainForNextUpdate()
    {
        var updater = new FakeUpdater { Available = "0.13.0" };
        var checker = new UpdateChecker(updater, _log.Add);
        await checker.CheckAsync(manual: false);

        updater.Available = null; // 適用した・取り下げられた
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await checker.CheckAsync(manual: false)).Outcome);

        updater.Available = "0.14.0";
        var next = await checker.CheckAsync(manual: false);
        Assert.True(next.NotifyBalloon);
        Assert.Equal("0.14.0", next.Version);
    }

    [Fact]
    public async Task Checker_WhileChecking_ReturnsBusyWithoutQueryingAgain()
    {
        var hold = new TaskCompletionSource();
        var updater = new FakeUpdater { Available = "0.13.0", HoldCheck = hold };
        var checker = new UpdateChecker(updater, _log.Add);

        var running = checker.CheckAsync(manual: false); // 自動の確認が照会中
        var busy = await checker.CheckAsync(manual: true);
        hold.SetResult();
        await running;

        Assert.Equal(UpdateCheckOutcome.Busy, busy.Outcome);
        Assert.Equal(1, updater.Checks);
    }

    [Fact]
    public async Task Checker_Failure_ReturnsFailedAndLogs_ThenCanCheckAgain()
    {
        var updater = new FakeUpdater { ThrowOnCheck = new HttpRequestException("401") };
        var checker = new UpdateChecker(updater, _log.Add);

        Assert.Equal(UpdateCheckOutcome.Failed, (await checker.CheckAsync(manual: true)).Outcome);
        Assert.Contains(_log, l => l.Contains("401"));

        updater.ThrowOnCheck = null;
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await checker.CheckAsync(manual: true)).Outcome); // 照会中のまま残らない
    }

    // ---- バージョン情報ページ ----

    [Fact]
    public void Page_ShowsVersion()
    {
        Assert.Equal("Radirec 0.12.0", Page(new FakeUpdater()).VersionText); // W-501
    }

    [Fact]
    public void Page_NotInstalled_ShowsNotUpdatable()
    {
        var page = Page(new FakeUpdater { IsInstalled = false, PendingVersion = "0.13.0" }, support: UpdateSupport.NotInstalled);

        Assert.False(page.CanUpdate);
        Assert.True(page.IsNotInstalled); // W-510
        Assert.False(page.IsConfigUnreadable);
        Assert.False(page.CheckCommand.CanExecute(null));
        Assert.False(page.HasPending);
        Assert.Equal("この版は自己更新の対象外です(開発版・portable)", UpdateTexts.NotUpdatable);
    }

    [Fact]
    public void Page_WithoutUpdater_NotInstalled_ShowsNotUpdatable()
    {
        // 開発版・portable で、さらに設定も読めない。更新できない根本の理由(W-510)を出す。
        var page = Page(updater: null, support: UpdateSupport.NotInstalled);

        Assert.False(page.CanUpdate);
        Assert.True(page.IsNotInstalled);
        Assert.False(page.IsConfigUnreadable);
    }

    [Fact]
    public void Page_WithoutUpdater_ConfigUnreadable_ShowsConfigReason()
    {
        // Velopack で入れた版で設定を読めない。「自己更新の対象外」ではなく、設定を読めないことを出す(W-511)。
        var page = Page(updater: null, support: UpdateSupport.ConfigUnreadable);

        Assert.False(page.CanUpdate);
        Assert.False(page.IsNotInstalled);
        Assert.True(page.IsConfigUnreadable);
        Assert.False(page.CheckCommand.CanExecute(null));
        Assert.Equal("設定を読めないため、更新を確認できません", UpdateTexts.ConfigUnreadable);
    }

    [Fact]
    public void Page_SupportedButCheckerMissing_HidesItemsWithoutReason()
    {
        // 設定は読めたが、更新の確認の初期化に失敗した(gui.log にある)。事実と違う理由は出さない。
        var page = new AboutPageViewModel("0.12.0", UpdateSupport.Supported, new FakeUpdater(), checker: null, _ => true, _log.Add);

        Assert.False(page.CanUpdate);
        Assert.False(page.IsNotInstalled);
        Assert.False(page.IsConfigUnreadable);
    }

    [Fact]
    public void Page_Installed_HasNoNote()
    {
        var page = Page(new FakeUpdater());

        Assert.True(page.CanUpdate);
        Assert.False(page.IsNotInstalled);
        Assert.False(page.IsConfigUnreadable);
        Assert.True(page.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task Check_UpToDate_ShowsMessageInPage()
    {
        var page = Page(new FakeUpdater());

        await page.CheckCommand.ExecuteAsync(null);

        Assert.Equal("最新版です。", page.Message); // W-503
        Assert.False(page.MessageIsWarning);
        Assert.False(page.HasPending);
    }

    [Fact]
    public async Task Check_Found_ShowsMessageAndApplyButton()
    {
        var page = Page(new FakeUpdater { Available = "0.13.0" });

        await page.CheckCommand.ExecuteAsync(null);

        Assert.Equal("更新があります(v0.13.0)。", page.Message); // W-504
        Assert.True(page.HasPending);
        Assert.Equal("更新 v0.13.0 を適用して再起動", page.ApplyButtonText); // W-507
        Assert.True(page.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Check_WhileAutoCheckRuns_ShowsBusy()
    {
        var hold = new TaskCompletionSource();
        var updater = new FakeUpdater { HoldCheck = hold };
        var checker = new UpdateChecker(updater, _log.Add);
        var page = Page(updater, checker: checker);

        var auto = checker.CheckAsync(manual: false);
        await page.CheckCommand.ExecuteAsync(null);
        hold.SetResult();
        await auto;

        Assert.Equal("更新を確認中です。少し待って再度お試しください。", page.Message); // W-505
    }

    [Fact]
    public async Task Check_Failure_ShowsWarning()
    {
        var page = Page(new FakeUpdater { ThrowOnCheck = new InvalidOperationException("net") });

        await page.CheckCommand.ExecuteAsync(null);

        Assert.Equal("更新チェックに失敗しました(gui.log 参照)。", page.Message); // W-506
        Assert.True(page.MessageIsWarning);
    }

    [Fact]
    public void Refresh_PicksUpUpdateFoundByAutoCheck()
    {
        var updater = new FakeUpdater();
        var page = Page(updater);
        Assert.False(page.HasPending);

        updater.PendingVersion = "0.13.0"; // ページを開いている間に、トレイの自動の確認が見つけた
        page.Refresh();

        Assert.True(page.HasPending);
        Assert.Equal("更新 v0.13.0 を適用して再起動", page.ApplyButtonText);
    }

    [Fact]
    public async Task Apply_Confirmed_AppliesAfterConfirm()
    {
        var updater = new FakeUpdater { PendingVersion = "0.13.0" };
        var page = Page(updater, confirm: true);

        await page.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(
            ["更新 v0.13.0 を適用してアプリを再起動します。取り込み中の走査は完了を待ってから安全に再起動します。よろしいですか?"],
            _confirms); // W-508
        Assert.Equal(1, updater.Applies);
    }

    [Fact]
    public async Task Apply_Cancelled_DoesNotApply()
    {
        var updater = new FakeUpdater { PendingVersion = "0.13.0" };
        var page = Page(updater, confirm: false);

        await page.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(0, updater.Applies);
        Assert.Null(page.Message);
    }

    [Fact]
    public async Task Apply_PendingWithdrawn_ShowsNothingToApplyWithoutConfirm()
    {
        var updater = new FakeUpdater { PendingVersion = "0.13.0" };
        var page = Page(updater);
        updater.PendingVersion = null; // 押すまでの間に取り下げられた

        await page.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("適用できる更新はありません(最新か、取り下げられました)。", page.Message); // W-509
        Assert.Empty(_confirms);
        Assert.Equal(0, updater.Applies);
        Assert.False(page.HasPending);
    }

    [Fact]
    public async Task Apply_Failure_ShowsWarningAndLogs()
    {
        var updater = new FakeUpdater { PendingVersion = "0.13.0", ThrowOnApply = new IOException("disk") };
        var page = Page(updater);

        await page.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("更新の適用に失敗しました(gui.log 参照)。", page.Message); // W-509
        Assert.True(page.MessageIsWarning);
        Assert.Contains(_log, l => l.Contains("disk"));
    }

    [Fact]
    public void FoundBalloon_MatchesMock()
    {
        Assert.Equal(
            "更新があります(v0.13.0)。Radirec を開いて「バージョン情報」か、トレイメニューから適用できます。",
            UpdateTexts.FoundBalloon("0.13.0")); // W-609
    }

    [Fact]
    public void ManagementViewModel_Tick_RefreshesAboutPage()
    {
        var updater = new FakeUpdater();
        var about = Page(updater);
        var vm = new ManagementViewModel(control: null, "設定を読み込めませんでした", loadHistory: null,
            new NoActions(), _log.Add, about: about);

        updater.PendingVersion = "0.13.0";
        vm.Tick();

        Assert.Same(about, vm.About);
        Assert.True(vm.About.HasPending);
    }

    [Fact]
    public void ManagementViewModel_WithoutAbout_HasPageWithoutUpdateItems()
    {
        var vm = new ManagementViewModel(control: null, "設定を読み込めませんでした", loadHistory: null,
            new NoActions(), _log.Add);

        Assert.False(vm.About.CanUpdate);
        Assert.StartsWith("Radirec ", vm.About.VersionText);
    }

    private sealed class NoActions : IHistoryPathActions
    {
        public Task<string> RevealAsync(string path) => Task.FromResult("");
        public bool TryCopy(string path) => true;
    }
}
