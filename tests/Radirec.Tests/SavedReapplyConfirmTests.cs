using System.Text;
using Radirec.Core.Attention;
using Radirec.Core.Configuration;
using Radirec.Core.Reapply;
using Radirec.Gui.Core.Management;
using Radirec.Hosting;
using Radirec.Hosting.Control;
using static Radirec.Tests.ServiceStatusTextTests;

namespace Radirec.Tests;

// 保存済みの設定での再適用の確認(#164)。設定ページの stations.json の「再読み込み」から、番組ページの確認の画面
// (W-252 / W-253 / W-312)へ進む。文言の ID は docs/mockups/management-window.md。
public sealed class SavedReapplyConfirmTests : IDisposable
{
    private const string Sample = """
        {
          "Programs": [
            { "Match": { "Station": "TBSラジオ", "Folders": ["パンサー向井のふらっと(1)", "パンサー向井のふらっと(2)"] },
              "Canonical": { "Album": "パンサー向井のふらっと" } }
          ]
        }
        """;

    // worker の代わり。保存済みの設定での確認は PlanReapplyAsync()(設定リロード → dry-run)、実行は ApplyReapplyAsync(plan)。
    private sealed class FakeControl : IServiceControl
    {
        public ServiceStatusSnapshot Status { get; set; } = Snapshot();
        public List<string> Calls { get; } = [];
        public Func<ReapplyResult>? PlanResult { get; set; }
        public Func<ReapplyReport, ReapplyResult>? ApplyResult { get; set; }
        public ReapplyReport Plan { get; set; } = Report(dryRun: true, ReapplyAction.Move, ReapplyAction.Move);
        public ReapplyReport? AppliedPlan { get; private set; }

        public void RequestRescan() => Calls.Add("rescan");
        public Task<ScanRunResult> RunScanAsync() => throw new NotSupportedException();
        public ServiceStatusSnapshot GetStatus() => Status;
        public ReloadResult RequestReload() => ReloadResult.Ok();

        public Task<ReapplyResult> PlanReapplyAsync()
        {
            Calls.Add("plan-saved");
            return Task.FromResult(PlanResult?.Invoke() ?? ReapplyResult.Ok(Plan));
        }

        public Task<ReapplyResult> PlanReapplyAsync(ProgramMasterConfig programs)
        {
            Calls.Add("plan-draft");
            return Task.FromResult(ReapplyResult.Ok(Plan));
        }

        public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan)
        {
            Calls.Add("apply");
            AppliedPlan = plan;
            return Task.FromResult(ApplyResult?.Invoke(plan) ?? ReapplyResult.Ok(Report(dryRun: false, ReapplyAction.Move, ReapplyAction.Move)));
        }
        public Task<ReapplyResult> PlanReapplyForCliAsync() => throw new NotSupportedException();
        public Task<ReapplyResult> ApplyReapplyPlanAsync(string planId) => throw new NotSupportedException();

        public Func<ReapplyResult>? ReloadAndApplyResult { get; set; }

        public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan)
        {
            Calls.Add("reload-and-apply");
            return Task.FromResult(ReloadAndApplyResult?.Invoke() ?? ReapplyResult.Ok(Report(dryRun: false, ReapplyAction.Move)));
        }
    }

    private sealed class FakeActions : ISettingsPageActions
    {
        public Task<SettingsFileOpenResult> OpenAsync(ConfigFile file, string path) => Task.FromResult(new SettingsFileOpenResult(true, null));
        public string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
        public bool ConfirmReload(string message) => throw new InvalidOperationException("stations.json の再読み込みは W-309 を出さない");
        public void Restart() { }
    }

    private sealed class NoActions : IHistoryPathActions
    {
        public Task<string> RevealAsync(string path) => Task.FromResult("");
        public bool TryCopy(string path) => true;
    }

    // 入口の代わり(設定ページだけを確かめるとき)。
    private sealed class FakeEntry : ISavedReapplyEntry
    {
        public string? BlockedReason { get; set; }
        public TaskCompletionSource<SavedReapplyStart> Pending { get; } = new();
        public int Starts { get; private set; }

        public Task<SavedReapplyStart> StartAsync()
        {
            Starts++;
            return Pending.Task;
        }
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("radirec-saved-reapply-").FullName;
    private readonly List<string> _log = [];
    private readonly List<ManagementPage> _navigated = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ProgramsPath => Path.Combine(_dir, "programs.json");
    private string StationsPath => Path.Combine(_dir, "stations.json");
    private string ConfigPath => Path.Combine(_dir, "radirec.json");

    private static ReapplyItem Item(ReapplyAction action) => new()
    {
        Action = action,
        Station = "TBSラジオ",
        FromAlbum = "安住紳一郎の日曜天国(1)",
        ToAlbum = "安住紳一郎の日曜天国",
        BroadcastDate = new DateOnly(2026, 9, 20),
        FromPath = @"Z:\a\x.m4a",
        ToPath = @"Z:\b\x.m4a",
        SourcePath = @"I:\TBSラジオ\安住紳一郎の日曜天国(1)\x.m4a",
    };

    private static ReapplyReport Report(bool dryRun, params ReapplyAction[] actions) => new()
    {
        Items = actions.Select(Item).ToList(),
        DryRun = dryRun,
        UnintegratedSiblings = [],
    };

    // programs.json の場所を決めた回数(= 読み込みを始めた回数)。
    private int _locates;

    private ProgramsPageViewModel Programs(FakeControl? control, Func<string, Task<ProgramsFileSnapshot>>? readForLoad = null)
    {
        File.WriteAllText(ProgramsPath, Sample, new UTF8Encoding(false));
        return new ProgramsPageViewModel(control, () => { _locates++; return ProgramsPath; }, new ProgramsApplyGate(), _log.Add,
            navigate: _navigated.Add, readForLoad: readForLoad);
    }

    // 読み込みを、テストが gate を開けるまで終わらせない(読み込みの速さに依らず、「読んでいる途中」を作る。#198)。
    private static Func<string, Task<ProgramsFileSnapshot>> ReadAfter(Task gate) =>
        async path =>
        {
            await gate.ConfigureAwait(false);
            return ProgramsFileWriter.Read(path);
        };

    // ---------- 番組ページの確認の画面(W-252 / W-253 / W-312) ----------

    [Fact]
    public async Task Changes_show_the_confirm_screen_with_saved_wording_and_apply_back_buttons()
    {
        var control = new FakeControl();
        var vm = Programs(control);

        var start = await vm.ConfirmSavedAsync(ManagementPage.Settings);

        Assert.True(start.ShowsConfirm);
        Assert.Null(start.Message);
        Assert.Equal(["plan-saved"], control.Calls); // 設定リロード → dry-run(下書きでは確かめない)
        Assert.True(vm.IsConfirmMode);
        Assert.True(vm.IsSavedBasis);
        Assert.False(vm.IsDraftBasis);
        Assert.Equal("2 回を今の設定に揃えます", vm.ConfirmHeading); // W-252
        Assert.StartsWith("今の programs.json と stations.json の設定で、取り込み済みの回を次のように揃えます。", vm.ConfirmBody); // W-253
        Assert.Equal("", vm.DiffBefore); // W-241 は出さない
        Assert.Equal("", vm.DiffAfter);
        Assert.Null(vm.SaveOnlyNote); // W-250 は出さない
        // W-312:保存するものが無いので「保存して適用」「保存だけ」は押せない。「適用」と「戻る」だけ
        Assert.True(vm.ApplySavedCommand.CanExecute(null));
        Assert.False(vm.SaveAndApplyCommand.CanExecute(null));
        Assert.False(vm.SaveOnlyCommand.CanExecute(null));
        Assert.True(vm.IsConfirmBackDefault); // 既定は「戻る」
    }

    [Fact]
    public async Task Apply_runs_the_plan_that_was_shown_and_shows_completion()
    {
        var control = new FakeControl();
        var vm = Programs(control);
        await vm.ConfirmSavedAsync(ManagementPage.Settings);
        var shown = control.Plan;

        await vm.ApplySavedCommand.ExecuteAsync(null);

        Assert.Same(shown, control.AppliedPlan); // 確認で見せた計画で実行する
        Assert.DoesNotContain("reload-and-apply", control.Calls); // 保存していないので「保存して適用」の経路は使わない
        Assert.True(vm.IsResultMode);
        Assert.Equal("統合設定の再適用が完了しました", vm.ResultHeading); // W-272
        Assert.False(vm.ResultIsError);
        Assert.Null(vm.SavedMessage); // 保存していないので W-270 は出さない
    }

    [Fact]
    public async Task Apply_does_not_run_when_settings_changed_after_confirm_W278()
    {
        var control = new FakeControl { ApplyResult = _ => ReapplyResult.Stale("確認のあとに設定が変わったため中止しました。") };
        var vm = Programs(control);
        await vm.ConfirmSavedAsync(ManagementPage.Settings);

        await vm.ApplySavedCommand.ExecuteAsync(null);

        Assert.True(vm.IsResultMode);
        Assert.True(vm.ResultIsError);
        Assert.Equal(ProgramsPageViewModel.SavedStale, vm.ResultHeading); // W-278(保存済みの設定の形)
    }

    [Fact]
    public async Task Back_from_confirm_returns_to_the_origin_page_and_the_list()
    {
        var control = new FakeControl();
        var vm = Programs(control);
        await vm.ConfirmSavedAsync(ManagementPage.Settings);

        await vm.BackCommand.ExecuteAsync(null);

        Assert.Equal([ManagementPage.Settings], _navigated);
        Assert.True(vm.IsListMode);
        Assert.False(vm.IsSavedBasis);
        Assert.DoesNotContain("apply", control.Calls);
    }

    [Fact]
    public async Task Back_from_result_returns_to_the_origin_page()
    {
        var control = new FakeControl();
        var vm = Programs(control);
        await vm.ConfirmSavedAsync(ManagementPage.Home);
        await vm.ApplySavedCommand.ExecuteAsync(null);

        await vm.BackCommand.ExecuteAsync(null);

        Assert.Equal([ManagementPage.Home], _navigated);
        Assert.True(vm.IsListMode);
        Assert.False(vm.IsSavedBasis);
    }

    [Fact]
    public async Task Aligned_stays_on_the_origin_page_with_one_line_W254()
    {
        var control = new FakeControl { Plan = Report(dryRun: true) };
        var vm = Programs(control);

        var start = await vm.ConfirmSavedAsync(ManagementPage.Settings);

        Assert.False(start.ShowsConfirm);
        Assert.False(start.IsWarning);
        Assert.Equal("取り込み済みの回は今の設定と揃っています", start.Message); // W-254
        Assert.True(vm.IsListMode);
        Assert.False(vm.IsSavedBasis);
        Assert.Empty(_navigated);
    }

    [Fact]
    public async Task Aligned_with_excluded_episodes_says_how_many_are_left_W254()
    {
        var control = new FakeControl { Plan = Report(dryRun: true, ReapplyAction.Excluded) };
        var vm = Programs(control);

        var start = await vm.ConfirmSavedAsync(ManagementPage.Settings);

        Assert.Equal("取り込み済みの回は今の設定と揃っています(対象外の回が 1 回あります。動かしません)", start.Message);
    }

    [Fact]
    public async Task Failure_stays_on_the_origin_page_with_the_reason_W255()
    {
        var control = new FakeControl { PlanResult = () => ReapplyResult.Failed("設定の読み込みに失敗しました: 壊れています") };
        var vm = Programs(control);

        var start = await vm.ConfirmSavedAsync(ManagementPage.Settings);

        Assert.False(start.ShowsConfirm);
        Assert.True(start.IsWarning);
        Assert.Equal("統合設定の再適用を中止しました: 設定の読み込みに失敗しました: 壊れています", start.Message); // W-255
        Assert.True(vm.IsListMode);
        Assert.False(vm.IsSavedBasis);
    }

    [Fact]
    public async Task Unsaved_changes_block_the_entry_and_keep_the_draft_W256()
    {
        var control = new FakeControl();
        var vm = Programs(control);
        await vm.LoadAsync();
        vm.SelectedProgram = vm.Programs[0];
        vm.DeleteProgramCommand.Execute(null);

        Assert.Equal(ProgramsPageViewModel.SavedConfirmBlocked, vm.SavedConfirmBlockedReason); // W-256
        var start = await vm.ConfirmSavedAsync(ManagementPage.Settings);

        Assert.False(start.ShowsConfirm);
        Assert.True(start.IsWarning);
        Assert.Equal(ProgramsPageViewModel.SavedConfirmBlocked, start.Message);
        Assert.Empty(control.Calls); // 確かめない
        Assert.True(vm.HasChanges); // 下書きは消さない
        Assert.True(vm.IsListMode);
    }

    [Fact]
    public async Task Editing_form_blocks_the_entry_W256()
    {
        var vm = Programs(new FakeControl());
        await vm.LoadAsync();
        vm.AddProgramCommand.Execute(null);

        Assert.Equal(ProgramsPageViewModel.SavedConfirmBlocked, vm.SavedConfirmBlockedReason);
    }

    [Fact]
    public async Task Not_loaded_yet_is_loaded_first_so_showing_the_page_keeps_the_confirm_screen()
    {
        var control = new FakeControl();
        var vm = Programs(control);

        await vm.ConfirmSavedAsync(ManagementPage.Settings);
        await vm.OnShownAsync(); // 呼び出し元が番組ページへ移ったときの読み込み

        Assert.True(vm.IsConfirmMode);
        Assert.True(vm.IsSavedBasis);
        Assert.True(vm.IsReady);
    }

    // 番組ページを開いた直後(まだ programs.json を読んでいる)に stations.json の「再読み込み」を押しても、読み終わった読み込みが
    // 確認の画面を一覧に戻さない(確認は読み込みが終わるのを待ってから進む)。
    [Fact]
    public void Confirm_waits_for_the_load_started_by_showing_the_page()
    {
        var control = new FakeControl();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Programs(control, ReadAfter(gate.Task));

        UiThreadContext.Run(async () =>
        {
            var shown = vm.OnShownAsync(); // 番組ページを開いた(読み込みが走る)
            Assert.False(shown.IsCompleted);

            var confirming = vm.ConfirmSavedAsync(ManagementPage.Settings);
            Assert.False(confirming.IsCompleted); // 読み込みが終わるのを待っている
            gate.SetResult();
            var start = await confirming;
            await shown;

            Assert.Equal(1, _locates); // 確認は走っている読み込みを待ち、読み直さない
            Assert.True(start.ShowsConfirm);
            Assert.True(vm.IsConfirmMode);
            Assert.True(vm.IsSavedBasis);
        });
    }

    // 番組ページを開いたときの読み込みが、確認を押すより先に終わっていても(読むのが速い・プールに空きがある)、確認は読み直さずに進む。
    [Fact]
    public void Confirm_after_the_load_started_by_showing_the_page_has_finished_does_not_load_again()
    {
        var control = new FakeControl();
        var vm = Programs(control, path => Task.FromResult(ProgramsFileWriter.Read(path)));

        UiThreadContext.Run(async () =>
        {
            var shown = vm.OnShownAsync(); // 読み込みがその場で終わる順序
            Assert.True(shown.IsCompleted);

            var start = await vm.ConfirmSavedAsync(ManagementPage.Settings);
            await shown;

            Assert.Equal(1, _locates);
            Assert.True(start.ShowsConfirm);
            Assert.True(vm.IsConfirmMode);
            Assert.True(vm.IsSavedBasis);
        });
    }

    // 確認の先読みの最中に番組のタブへ切り替えても、2 本目の読み込みは走らず、確認の画面が一覧に戻らない。
    [Fact]
    public void Showing_the_page_during_the_confirm_preload_does_not_start_a_second_load()
    {
        var control = new FakeControl();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Programs(control, ReadAfter(gate.Task));

        UiThreadContext.Run(async () =>
        {
            var confirming = vm.ConfirmSavedAsync(ManagementPage.Settings); // まだ読んでいないので先に読む
            Assert.False(confirming.IsCompleted);
            var shown = vm.OnShownAsync(); // その間に番組のタブへ切り替えた
            gate.SetResult();

            var start = await confirming;
            await shown;

            Assert.Equal(1, _locates); // 読み込みは 1 回だけ(走っている読み込みを待った)
            Assert.True(start.ShowsConfirm);
            Assert.True(vm.IsConfirmMode);
            Assert.True(vm.IsSavedBasis);
        });
    }

    // 「保存して適用」で保存できたのに実行できなかった後、保存済みの設定で実行できたら、変更が無いのに「確認」を押せる状態を下ろす。
    [Fact]
    public async Task Saved_apply_clears_the_pending_reapply_after_a_failed_save_and_apply()
    {
        var control = new FakeControl { ReloadAndApplyResult = () => ReapplyResult.Failed("設定の反映に失敗しました: x") };
        var vm = Programs(control);
        await vm.LoadAsync();
        vm.SelectedProgram = vm.Programs[0];
        vm.DeleteProgramCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        await vm.SaveAndApplyCommand.ExecuteAsync(null);
        await vm.BackCommand.ExecuteAsync(null);
        Assert.False(vm.HasChanges);
        Assert.True(vm.CanConfirm); // 保存したのに実行できなかった(変更が無くても「確認」から実行し直せる)

        await vm.ConfirmSavedAsync(ManagementPage.Settings);
        await vm.ApplySavedCommand.ExecuteAsync(null);
        await vm.BackCommand.ExecuteAsync(null);

        Assert.True(vm.IsListMode);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task Draft_confirm_after_saved_confirm_uses_the_draft_wording_again()
    {
        var control = new FakeControl();
        var vm = Programs(control);
        await vm.ConfirmSavedAsync(ManagementPage.Settings);
        await vm.BackCommand.ExecuteAsync(null);

        vm.SelectedProgram = vm.Programs[0];
        vm.DeleteProgramCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.True(vm.IsConfirmMode);
        Assert.False(vm.IsSavedBasis);
        Assert.Equal("2 回を編集した設定に揃えます", vm.ConfirmHeading); // W-242
        Assert.True(vm.SaveAndApplyCommand.CanExecute(null));
        Assert.False(vm.ApplySavedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stopped_worker_cannot_confirm()
    {
        var vm = Programs(control: null);

        var start = await vm.ConfirmSavedAsync(ManagementPage.Settings);

        Assert.False(start.ShowsConfirm);
        Assert.True(start.IsWarning);
        Assert.Equal("統合設定の再適用を中止しました: " + ProgramsPageViewModel.ConfirmUnavailable, start.Message);
    }

    // ---------- 設定ページの stations.json の「再読み込み」 ----------

    // 開く場所は、取り込みが今読み込んでいる設定(状態)から決まる。stationsFile を空にすると stations.json を使わない設定。
    private SettingsPageViewModel Settings(FakeControl control, string? stationsFile = null)
    {
        stationsFile ??= StationsPath;
        control.Status = control.Status with { StationsFile = stationsFile, ProgramsFile = ProgramsPath };
        return new(control, ConfigPath, new RadirecConfig { StationsFile = stationsFile, ProgramsFile = ProgramsPath },
            configError: null, IngestToastSetting.Fixed(true), new FakeActions(), _log.Add);
    }

    [Fact]
    public async Task Stations_reload_shows_W311_while_checking_then_the_result_line()
    {
        var page = Settings(new FakeControl());
        var entry = new FakeEntry();
        page.SavedReapply = entry;
        Assert.True(page.ReloadStationsCommand.CanExecute(null));

        var running = page.ReloadStationsCommand.ExecuteAsync(null);

        Assert.Equal(1, entry.Starts);
        Assert.Equal(SettingsPageViewModel.StationsReloadChecking, page.Stations.Message); // W-311
        Assert.False(page.CanReloadStations); // 確かめている途中は押せない

        entry.Pending.SetResult(SavedReapplyStart.Stay("取り込み済みの回は今の設定と揃っています", isWarning: false));
        await running;

        Assert.Equal("取り込み済みの回は今の設定と揃っています", page.Stations.Message); // W-254
        Assert.False(page.Stations.IsMessageWarning);
        Assert.True(page.CanReloadStations);
    }

    [Fact]
    public async Task Stations_reload_clears_the_line_when_moving_to_the_confirm_screen()
    {
        var page = Settings(new FakeControl());
        var entry = new FakeEntry();
        page.SavedReapply = entry;

        var running = page.ReloadStationsCommand.ExecuteAsync(null);
        entry.Pending.SetResult(SavedReapplyStart.Confirm);
        await running;

        Assert.Null(page.Stations.Message);
    }

    [Fact]
    public async Task Stations_reload_failure_is_a_warning_line_W255()
    {
        var page = Settings(new FakeControl());
        var entry = new FakeEntry();
        page.SavedReapply = entry;

        var running = page.ReloadStationsCommand.ExecuteAsync(null);
        entry.Pending.SetResult(SavedReapplyStart.Stay("統合設定の再適用を中止しました: x", isWarning: true));
        await running;

        Assert.Equal("統合設定の再適用を中止しました: x", page.Stations.Message);
        Assert.True(page.Stations.IsMessageWarning);
    }

    [Fact]
    public void Stations_reload_is_disabled_with_the_reason_while_programs_page_has_unsaved_work_W256()
    {
        var page = Settings(new FakeControl());
        var entry = new FakeEntry { BlockedReason = ProgramsPageViewModel.SavedConfirmBlocked };
        page.SavedReapply = entry;

        Assert.False(page.ReloadStationsCommand.CanExecute(null));
        Assert.Equal(ProgramsPageViewModel.SavedConfirmBlocked, page.StationsReloadNote);

        // 番組ページで変更をやめたら、タイマーの読み直しで押せるようになる。
        entry.BlockedReason = null;
        page.RefreshSavedReapply();

        Assert.True(page.ReloadStationsCommand.CanExecute(null));
        Assert.Null(page.StationsReloadNote);
    }

    [Fact]
    public void Stations_reload_is_disabled_while_the_worker_is_stopped_W316()
    {
        var page = Settings(new FakeControl { Status = Snapshot(running: false) });
        page.SavedReapply = new FakeEntry();

        Assert.False(page.ReloadStationsCommand.CanExecute(null));
        Assert.Equal(SettingsPageViewModel.ReloadUnavailable, page.StationsReloadNote);
    }

    [Fact]
    public void Stations_reload_is_disabled_when_stations_json_is_not_used()
    {
        var page = Settings(new FakeControl(), stationsFile: "");
        page.SavedReapply = new FakeEntry();

        Assert.False(page.ReloadStationsCommand.CanExecute(null));
        Assert.Null(page.StationsReloadNote); // W-307 が段に出るので、ボタンの横には何も出さない
    }

    [Fact]
    public void Stations_reload_is_disabled_until_an_entry_is_connected()
    {
        var page = Settings(new FakeControl());

        Assert.False(page.ReloadStationsCommand.CanExecute(null));
    }

    // ---------- 管理画面でつないだとき ----------

    private ManagementViewModel Management(FakeControl control, SettingsPageViewModel settings)
    {
        File.WriteAllText(ProgramsPath, Sample, new UTF8Encoding(false));
        return new ManagementViewModel(control, null, null, new NoActions(), _log.Add,
            locateProgramsFile: () => ProgramsPath, settings: settings);
    }

    [Fact]
    public async Task Settings_stations_reload_moves_to_the_programs_confirm_and_back_returns_to_settings()
    {
        var control = new FakeControl();
        var settings = Settings(control);
        var vm = Management(control, settings);
        vm.CurrentPage = ManagementPage.Settings;

        await settings.ReloadStationsCommand.ExecuteAsync(null);

        Assert.Equal(ManagementPage.Programs, vm.CurrentPage);
        Assert.True(vm.Programs.IsConfirmMode);
        Assert.True(vm.Programs.IsSavedBasis);
        Assert.Null(settings.Stations.Message);

        await vm.Programs.BackCommand.ExecuteAsync(null);

        Assert.Equal(ManagementPage.Settings, vm.CurrentPage);
        Assert.True(vm.Programs.IsListMode);
    }

    [Fact]
    public async Task Settings_stations_reload_with_nothing_to_align_stays_on_settings()
    {
        var control = new FakeControl { Plan = Report(dryRun: true) };
        var settings = Settings(control);
        var vm = Management(control, settings);
        vm.CurrentPage = ManagementPage.Settings;

        await settings.ReloadStationsCommand.ExecuteAsync(null);

        Assert.Equal(ManagementPage.Settings, vm.CurrentPage);
        Assert.Equal("取り込み済みの回は今の設定と揃っています", settings.Stations.Message); // W-254
    }

    [Fact]
    public async Task Programs_page_unsaved_work_disables_stations_reload_after_the_tick()
    {
        var control = new FakeControl();
        var settings = Settings(control);
        var vm = Management(control, settings);
        await vm.Programs.LoadAsync();
        Assert.True(settings.ReloadStationsCommand.CanExecute(null));

        vm.Programs.SelectedProgram = vm.Programs.Programs[0];
        vm.Programs.DeleteProgramCommand.Execute(null);
        vm.Tick();

        Assert.False(settings.ReloadStationsCommand.CanExecute(null));
        Assert.Equal(ProgramsPageViewModel.SavedConfirmBlocked, settings.StationsReloadNote); // W-256
    }

    [Fact]
    public async Task Entry_for_home_returns_to_home()
    {
        // 要対応の「再適用の確認へ」(W-136。#156)が使う形。
        var control = new FakeControl();
        var vm = Management(control, Settings(control));
        var entry = vm.SavedReapplyEntry(ManagementPage.Home);
        Assert.Null(entry.BlockedReason);

        var start = await entry.StartAsync();

        Assert.True(start.ShowsConfirm);
        Assert.Equal(ManagementPage.Programs, vm.CurrentPage);
        await vm.Programs.BackCommand.ExecuteAsync(null);
        Assert.Equal(ManagementPage.Home, vm.CurrentPage);
    }

    [Fact]
    public async Task Home_reapply_confirm_moves_to_the_programs_confirm_and_back_returns_home_and_recomputes_W136()
    {
        // ホームの要対応の「再適用の確認へ」(W-136。#156)。管理画面が、ホームを来たページにした入口をつなぐ。
        var control = new FakeControl();
        File.WriteAllText(ProgramsPath, Sample, new UTF8Encoding(false));
        var computes = 0;
        var vm = new ManagementViewModel(control, null, null, new NoActions(), _log.Add,
            locateProgramsFile: () => ProgramsPath, computeAttention: () =>
            {
                computes++;
                return new AttentionItems { SettingsDrift = [new SettingsDriftGroup("TBSラジオ", "前", "後", 2)] };
            });
        await vm.Home.AttentionRefreshed;
        Assert.True(vm.Home.CanReapply);
        var before = computes;

        await vm.Home.ReapplyCommand.ExecuteAsync(null);

        Assert.Contains("plan-saved", control.Calls);
        Assert.Equal(ManagementPage.Programs, vm.CurrentPage);
        Assert.True(vm.Programs.IsConfirmMode);
        Assert.True(vm.Programs.IsSavedBasis);
        Assert.False(vm.Home.CanReapply); // 確認の途中は押せない(W-256)

        await vm.Programs.BackCommand.ExecuteAsync(null);

        Assert.Equal(ManagementPage.Home, vm.CurrentPage);
        await vm.Home.AttentionRefreshed;
        Assert.True(computes > before); // ホームへ戻ったら要対応を計算し直す
    }
}
