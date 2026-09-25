using Radirec.Core.Configuration;
using Radirec.Core.Reapply;
using Radirec.Gui.Core;
using Radirec.Gui.Core.Management;
using Radirec.Hosting.Control;
using static Radirec.Tests.ServiceStatusTextTests;

namespace Radirec.Tests;

// 管理画面の ViewModel(#151)。文言の ID は docs/mockups/management-window.md。
public sealed class ManagementViewModelTests
{
    private static readonly DateTimeOffset Scan1 = new(2026, 9, 25, 1, 40, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Scan2 = new(2026, 9, 25, 1, 42, 0, TimeSpan.Zero);

    private sealed class FakeControl : IServiceControl
    {
        public ServiceStatusSnapshot Status { get; set; } = Snapshot();
        public int RescanRequests { get; private set; }
        public Exception? ThrowOnRescan { get; set; }
        public Exception? ThrowOnStatus { get; set; }

        public void RequestRescan()
        {
            if (ThrowOnRescan is { } ex)
                throw ex;
            RescanRequests++;
        }

        public ServiceStatusSnapshot GetStatus() => ThrowOnStatus is { } ex ? throw ex : Status;

        public ReloadResult RequestReload() => throw new NotSupportedException();
        public Task<ReapplyResult> PlanReapplyAsync() => throw new NotSupportedException();
        public Task<ReapplyResult> PlanReapplyAsync(ProgramMasterConfig programs) => throw new NotSupportedException();
        public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
        public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
    }

    private sealed class FakeLedger
    {
        public List<IngestHistoryRow> Rows { get; } = [];
        public int Loads { get; private set; }
        public Exception? Throw { get; set; }

        public IReadOnlyList<IngestHistoryRow> Load()
        {
            Loads++;
            if (Throw is { } ex)
                throw ex;
            return Rows.ToList();
        }
    }

    private sealed class FakeActions : IHistoryPathActions
    {
        public List<string> Revealed { get; } = [];
        public List<string> Copied { get; } = [];
        public bool CopyResult { get; set; } = true;
        public Exception? ThrowOnReveal { get; set; }

        public Task<string> RevealAsync(string path)
        {
            if (ThrowOnReveal is { } ex)
                throw ex;
            Revealed.Add(path);
            return Task.FromResult($"エクスプローラーで開きました: {PathReveal.FileName(path)}");
        }

        public bool TryCopy(string path)
        {
            Copied.Add(path);
            return CopyResult;
        }
    }

    private readonly List<string> _log = [];

    private static IngestHistoryRow Row(int n) => new()
    {
        Program = $"番組{n}",
        Station = "TBSラジオ",
        Broadcast = "2026-09-21 10:00",
        ImportedAt = "2026-09-25 10:42",
        OutputPath = $@"\\nas\share\radio\番組{n}_20260921.m4a",
        SourcePath = $@"I:\radio\tmp\RADIO\番組{n}.m4a",
    };

    private static FakeLedger LedgerWith(int count)
    {
        var ledger = new FakeLedger();
        for (var i = 1; i <= count; i++)
            ledger.Rows.Add(Row(i));
        return ledger;
    }

    private ManagementViewModel Create(
        IServiceControl? control, FakeLedger? ledger, FakeActions? actions = null, string? degradedReason = null) =>
        new(control, degradedReason, ledger is null ? null : ledger.Load, actions ?? new FakeActions(), _log.Add, Jst);

    // ---------- 枠 ----------

    [Fact]
    public void Starts_on_home_and_show_all_moves_to_history_page()
    {
        var vm = Create(new FakeControl(), LedgerWith(1));

        Assert.Equal(ManagementPage.Home, vm.CurrentPage);
        Assert.True(vm.IsHomePage);

        vm.Home.ShowAllCommand.Execute(null); // W-142

        Assert.Equal(ManagementPage.History, vm.CurrentPage);
        Assert.Equal(1, vm.CurrentPageIndex);
        Assert.True(vm.IsHistoryPage);
        Assert.False(vm.IsHomePage);
    }

    [Fact]
    public void Five_pages_in_mock_order()
    {
        // W-002
        Assert.Equal(["ホーム", "取込履歴", "番組", "設定", "バージョン情報"], ManagementViewModel.PageNames);
        Assert.Equal(ManagementViewModel.PageNames.Count, Enum.GetValues<ManagementPage>().Length);
    }

    [Fact]
    public void Page_index_out_of_range_is_ignored()
    {
        var vm = Create(new FakeControl(), LedgerWith(1));

        vm.CurrentPageIndex = -1; // 一覧の選択が外れた

        Assert.Equal(ManagementPage.Home, vm.CurrentPage);
    }

    // ---------- ホーム:状態と再スキャン ----------

    [Fact]
    public void Home_shows_status_from_worker()
    {
        var control = new FakeControl { Status = Snapshot(completed: Scan2, trigger: "検知", imported: 3) };

        var home = Create(control, LedgerWith(0)).Home;

        Assert.Equal("状態: 稼働中(監視中)", home.StatusText);
        Assert.Equal("最終スキャン: 9/25 10:42 検知(取込 3)", home.LastScanText);
        Assert.True(home.CanRescan);
        Assert.True(home.RescanCommand.CanExecute(null));
        Assert.Null(home.RescanNote);
    }

    [Fact]
    public void Rescan_shows_W111_in_window_until_next_scan_completes()
    {
        var control = new FakeControl { Status = Snapshot(completed: Scan1, trigger: "定期") };
        var vm = Create(control, LedgerWith(0));

        vm.Home.RescanCommand.Execute(null);

        Assert.Equal(1, control.RescanRequests);
        Assert.Equal("手動再スキャンを要求しました", vm.Home.RescanNote);

        vm.Tick(); // まだ同じスキャン
        Assert.Equal("手動再スキャンを要求しました", vm.Home.RescanNote);

        control.Status = Snapshot(completed: Scan2, trigger: "手動");
        vm.Tick();
        Assert.Null(vm.Home.RescanNote);
        Assert.Equal("最終スキャン: 9/25 10:42 手動(取込 0)", vm.Home.LastScanText);
    }

    [Fact]
    public void Rescan_before_first_scan_clears_when_first_scan_completes()
    {
        var control = new FakeControl { Status = Snapshot() }; // 未走査
        var vm = Create(control, LedgerWith(0));

        vm.Home.RescanCommand.Execute(null);
        vm.Tick();
        Assert.Equal(HomeViewModel.RescanRequested, vm.Home.RescanNote);

        control.Status = Snapshot(completed: Scan1, trigger: "手動");
        vm.Tick();
        Assert.Null(vm.Home.RescanNote);
    }

    [Fact]
    public void Rescan_exception_is_shown_in_window_and_logged()
    {
        var control = new FakeControl { ThrowOnRescan = new InvalidOperationException("boom") };
        var vm = Create(control, LedgerWith(0));

        vm.Home.RescanCommand.Execute(null); // 例外を漏らさない

        Assert.Equal(HomeViewModel.RescanFailed, vm.Home.RescanNote);
        Assert.Contains(_log, l => l.Contains("boom"));
    }

    [Fact]
    public void Crashed_worker_disables_rescan_with_W112()
    {
        var control = new FakeControl { Status = Snapshot(running: false) };

        var home = Create(control, LedgerWith(1)).Home;

        Assert.Equal("状態: 停止(取り込みが異常終了。gui.log 参照 → 再起動)", home.StatusText);
        Assert.Equal("最終スキャン: —", home.LastScanText);
        Assert.False(home.CanRescan);
        Assert.False(home.RescanCommand.CanExecute(null));
        Assert.Equal("取り込みが止まっているため、再スキャンできません", home.RescanNote);
        Assert.True(home.IsHistoryLoaded); // 設定は読めているので取込履歴は出す
    }

    [Fact]
    public void Worker_crashing_while_open_switches_to_W112()
    {
        var control = new FakeControl { Status = Snapshot(completed: Scan1) };
        var vm = Create(control, LedgerWith(0));
        vm.Home.RescanCommand.Execute(null);

        control.Status = Snapshot(running: false, completed: Scan1);
        vm.Tick();

        Assert.False(vm.Home.CanRescan);
        Assert.Equal(HomeViewModel.RescanUnavailable, vm.Home.RescanNote);
    }

    [Fact]
    public void Start_failed_W104_still_shows_history()
    {
        var home = Create(control: null, LedgerWith(2), degradedReason: ServiceStatusText.DegradedStartFailed).Home;

        Assert.Equal("状態: 取り込み停止中(取り込みの起動に失敗しました(設定ページ参照))", home.StatusText);
        Assert.Equal("最終スキャン: —", home.LastScanText);
        Assert.False(home.CanRescan);
        Assert.Equal(HomeViewModel.RescanUnavailable, home.RescanNote);
        Assert.True(home.IsHistoryLoaded);
        Assert.Equal(2, home.RecentRows.Count);
    }

    [Fact]
    public void Config_unreadable_W105_W108_W150()
    {
        var vm = Create(control: null, ledger: null, degradedReason: ServiceStatusText.DegradedConfigUnreadable);

        Assert.Equal("状態: 取り込み停止中(設定を読み込めませんでした)", vm.Home.StatusText);
        Assert.Equal("最終スキャン: —", vm.Home.LastScanText);
        Assert.Equal(HomeViewModel.RescanUnavailable, vm.Home.RescanNote);
        Assert.True(vm.Home.IsHistoryUnavailable);
        Assert.Null(vm.Home.RecentCaption); // W-140 の件数は出さない(W-150 に替わる)
        Assert.True(vm.History.IsUnavailable);
        Assert.False(vm.History.IsAvailable); // 「更新」も出さない(W-143)
        Assert.False(vm.History.RefreshCommand.CanExecute(null));

        vm.Tick(); // ワーカーが無くても落ちない
        Assert.Equal("状態: 取り込み停止中(設定を読み込めませんでした)", vm.Home.StatusText);
    }

    [Fact]
    public void Status_read_failure_keeps_previous_text_and_logs()
    {
        var control = new FakeControl { Status = Snapshot(completed: Scan1, trigger: "定期") };
        var vm = Create(control, LedgerWith(0));
        var before = vm.Home.LastScanText;

        control.ThrowOnStatus = new InvalidOperationException("status");
        vm.Tick();

        Assert.Equal(before, vm.Home.LastScanText);
        Assert.Contains(_log, l => l.Contains("status"));
    }

    // ---------- ホーム:取込履歴(新しい方の 5 件) ----------

    [Fact]
    public void Home_shows_newest_five_rows()
    {
        var home = Create(new FakeControl(), LedgerWith(8)).Home;

        Assert.Equal(["番組1", "番組2", "番組3", "番組4", "番組5"], home.RecentRows.Select(r => r.Program));
        Assert.Equal("新しい方の 5 件", home.RecentCaption); // W-140
    }

    [Fact]
    public void Home_reloads_history_only_when_last_scan_changes()
    {
        var control = new FakeControl { Status = Snapshot(completed: Scan1) };
        var ledger = LedgerWith(1);
        var vm = Create(control, ledger);
        var loadsAfterOpen = ledger.Loads; // ホームと取込履歴ページで 1 回ずつ

        vm.Tick();
        vm.Tick();
        Assert.Equal(loadsAfterOpen, ledger.Loads);

        ledger.Rows.Insert(0, Row(99));
        control.Status = Snapshot(completed: Scan2);
        vm.Tick();

        Assert.Equal(loadsAfterOpen + 1, ledger.Loads);
        Assert.Equal("番組99", vm.Home.RecentRows[0].Program);
        // 取込履歴ページの全件は「更新」でだけ読み直す(見ている最中に行がずれないように)
        Assert.Single(vm.History.Rows);
    }

    [Fact]
    public void Home_history_failure_W149_then_recovers_on_next_scan()
    {
        var control = new FakeControl { Status = Snapshot(completed: Scan1) };
        var ledger = LedgerWith(1);
        ledger.Throw = new IOException("locked");

        var vm = Create(control, ledger);
        Assert.True(vm.Home.IsHistoryFailed);
        Assert.Null(vm.Home.RecentCaption);
        Assert.Contains(_log, l => l.Contains("locked"));

        ledger.Throw = null;
        control.Status = Snapshot(completed: Scan2);
        vm.Tick();

        Assert.True(vm.Home.IsHistoryLoaded);
        Assert.Equal("新しい方の 1 件", vm.Home.RecentCaption);
    }

    // ---------- 取込履歴ページ ----------

    [Fact]
    public void History_page_shows_all_rows_and_count_W151()
    {
        var history = Create(new FakeControl(), LedgerWith(8)).History;

        Assert.True(history.IsLoaded);
        Assert.Equal(8, history.Rows.Count);
        Assert.Equal("8 件", history.CountText);
    }

    [Fact]
    public void History_page_reloads_on_refresh()
    {
        var ledger = LedgerWith(1);
        var history = Create(new FakeControl(), ledger).History;
        ledger.Rows.Add(Row(2));

        history.RefreshCommand.Execute(null); // W-143

        Assert.Equal(2, history.Rows.Count);
        Assert.Equal("2 件", history.CountText);
    }

    [Fact]
    public void History_page_load_failure_W149_keeps_refresh()
    {
        var ledger = LedgerWith(1);
        ledger.Throw = new IOException("locked");

        var history = Create(new FakeControl(), ledger).History;

        Assert.Equal(HistoryLoadState.Failed, history.State);
        Assert.Equal("読み込みに失敗しました", history.CountText);
        Assert.True(history.IsAvailable); // 読み直すために「更新」は出す
        Assert.True(history.RefreshCommand.CanExecute(null));
        Assert.False(history.IsLoaded);

        ledger.Throw = null;
        history.RefreshCommand.Execute(null);
        Assert.True(history.IsLoaded);
        Assert.Equal("1 件", history.CountText);
    }

    [Fact]
    public void Selecting_row_fills_detail_and_enables_menu()
    {
        var history = Create(new FakeControl(), LedgerWith(2)).History;

        Assert.False(history.RevealOutputCommand.CanExecute(null));
        Assert.False(history.CopySourceCommand.CanExecute(null));
        Assert.Equal("", history.SelectedOutputPath);

        history.SelectedRow = history.Rows[1];

        Assert.Equal(@"\\nas\share\radio\番組2_20260921.m4a", history.SelectedOutputPath); // W-145
        Assert.Equal(@"I:\radio\tmp\RADIO\番組2.m4a", history.SelectedSourcePath);
        Assert.True(history.RevealOutputCommand.CanExecute(null));
        Assert.True(history.RevealSourceCommand.CanExecute(null));
        Assert.True(history.CopyOutputCommand.CanExecute(null));
        Assert.True(history.CopySourceCommand.CanExecute(null));
    }

    [Fact]
    public async Task Reveal_output_and_source_show_result_in_window()
    {
        var actions = new FakeActions();
        var history = Create(new FakeControl(), LedgerWith(1), actions).History;
        history.SelectedRow = history.Rows[0];

        await history.RevealOutputCommand.ExecuteAsync(null);
        Assert.Equal("エクスプローラーで開きました: 番組1_20260921.m4a", history.ActionMessage); // W-146

        await history.RevealSourceCommand.ExecuteAsync(null);
        Assert.Equal([@"\\nas\share\radio\番組1_20260921.m4a", @"I:\radio\tmp\RADIO\番組1.m4a"], actions.Revealed);
    }

    [Fact]
    public async Task Reveal_exception_is_shown_in_window_and_logged()
    {
        var actions = new FakeActions { ThrowOnReveal = new InvalidOperationException("shell") };
        var history = Create(new FakeControl(), LedgerWith(1), actions).History;
        history.SelectedRow = history.Rows[0];

        await history.RevealOutputCommand.ExecuteAsync(null); // 例外を漏らさない

        Assert.Equal("エクスプローラーを開けませんでした(gui.log 参照)", history.ActionMessage); // W-147
        Assert.Contains(_log, l => l.Contains("shell"));
    }

    [Fact]
    public void Copy_reports_success_and_failure_W148()
    {
        var actions = new FakeActions();
        var history = Create(new FakeControl(), LedgerWith(1), actions).History;
        history.SelectedRow = history.Rows[0];

        history.CopyOutputCommand.Execute(null);
        Assert.Equal("パスをコピーしました", history.ActionMessage);
        Assert.Equal([@"\\nas\share\radio\番組1_20260921.m4a"], actions.Copied);

        actions.CopyResult = false;
        history.CopySourceCommand.Execute(null);
        Assert.Equal("パスをコピーできませんでした", history.ActionMessage);
    }

    [Fact]
    public void Refresh_clears_selection_and_action_message()
    {
        var history = Create(new FakeControl(), LedgerWith(1)).History;
        history.SelectedRow = history.Rows[0];
        history.CopyOutputCommand.Execute(null);

        history.RefreshCommand.Execute(null);

        Assert.Null(history.SelectedRow);
        Assert.Equal("", history.ActionMessage);
        Assert.Equal("", history.SelectedOutputPath);
    }
}
