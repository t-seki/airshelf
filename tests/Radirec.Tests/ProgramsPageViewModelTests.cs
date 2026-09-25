using System.Runtime.ExceptionServices;
using System.Text;
using Radirec.Core.Configuration;
using Radirec.Core.Reapply;
using Radirec.Gui.Core;
using Radirec.Gui.Core.Management;
using Radirec.Hosting;
using Radirec.Hosting.Control;
using static Radirec.Tests.ServiceStatusTextTests;

namespace Radirec.Tests;

// 管理画面の番組ページ(#154)。文言の ID は docs/mockups/management-window.md。programs.json は一時フォルダに実物を置く。
public sealed class ProgramsPageViewModelTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 25, 10, 45, 12);

    private const string Sample = """
        {
          "Programs": [
            { "Match": { "Station": "J-WAVE", "Folders": ["LOGISTEEDRADIONOMICS"] },
              "Canonical": { "Album": "LOGISTEED RADIONOMICS" }, "Note": "空白を入れて読みやすく" },
            { "Match": { "Station": "TBSラジオ", "Folders": ["パンサー向井のふらっと(1)", "パンサー向井のふらっと(2)"] },
              "Canonical": { "Album": "パンサー向井のふらっと" } }
          ],
          "KeepSeparate": [
            { "Match": { "Station": "文化放送", "Folders": ["大竹まこと ゴールデンラジオ!(1)", "大竹まこと ゴールデンラジオ!(2)"] } }
          ]
        }
        """;

    // worker の代わり。「保存して適用」は ReloadAndApplyReapplyAsync(1 つのジョブで設定リロード → 再適用)で呼ばれる。
    // ReloadError を立てると、worker と同じく設定リロードに失敗して再適用を実行しない。失敗は worker の _lastReloadError と
    // 同じく残り(LastReloadError)、次にリロードに成功するまで消えない。確認は worker と同じ ReapplyPlanGate で判定する。
    private sealed class FakeControl : IServiceControl
    {
        public ServiceStatusSnapshot Status { get; set; } = Snapshot();
        public List<string> Calls { get; } = [];
        public ProgramMasterConfig? PlannedWith { get; private set; }
        public ReapplyReport? AppliedPlan { get; private set; }
        public Func<ReapplyResult>? PlanResult { get; set; }
        public Func<ReapplyReport, ReapplyResult>? ApplyResult { get; set; }
        public string? ReloadError { get; set; }
        public string? LastReloadError { get; private set; }
        public ReapplyReport Plan { get; set; } = Report(dryRun: true, ReapplyAction.Move, ReapplyAction.Move);

        public void RequestRescan() => Calls.Add("rescan");
        public Task<ScanRunResult> RunScanAsync() => throw new NotSupportedException();
        public ServiceStatusSnapshot GetStatus() => Status;

        public ReloadResult RequestReload()
        {
            Calls.Add("reload");
            return ReloadResult.Ok();
        }

        public Task<ReapplyResult> PlanReapplyAsync() => throw new NotSupportedException();

        public Task<ReapplyResult> PlanReapplyAsync(ProgramMasterConfig programs)
        {
            Calls.Add("plan");
            PlannedWith = programs;
            if (ReapplyPlanGate.BlockingReloadError(hasDraft: true, LastReloadError) is { } blocking)
                return Task.FromResult(ReapplyResult.Failed($"設定の読み込みに失敗しました: {blocking}"));
            return Task.FromResult(PlanResult?.Invoke() ?? ReapplyResult.Ok(Plan));
        }

        public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();

        public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan)
        {
            Calls.Add("reload-and-apply");
            LastReloadError = ReloadError; // 実行の前に必ずリロードを試し直す。成功したときだけ消える
            if (ReloadError is { } error)
                return Task.FromResult(ReapplyResult.Failed($"設定の反映に失敗しました: {error}(取り込みは前の設定のまま動いています)"));

            AppliedPlan = plan;
            return Task.FromResult(ApplyResult?.Invoke(plan) ?? ReapplyResult.Ok(Report(dryRun: false, ReapplyAction.Move, ReapplyAction.Move)));
        }
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("radirec-programs-page-").FullName;
    private readonly List<string> _log = [];
    private readonly ProgramsApplyGate _gate = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ProgramsPath => Path.Combine(_dir, "programs.json");

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

    private async Task<ProgramsPageViewModel> CreateLoaded(FakeControl? control, string? content = Sample)
    {
        if (content is not null)
            File.WriteAllText(ProgramsPath, content, new UTF8Encoding(false));
        var vm = new ProgramsPageViewModel(control, () => ProgramsPath, _gate, _log.Add, now: () => Now);
        await vm.LoadAsync();
        return vm;
    }

    private static void AddAnzu(ProgramsPageViewModel vm)
    {
        vm.AddProgramCommand.Execute(null);
        vm.FormStation = "TBSラジオ";
        vm.FormFolders = "安住紳一郎の日曜天国(1)\r\n安住紳一郎の日曜天国(2)\r\n";
        vm.FormAlbum = "安住紳一郎の日曜天国";
        vm.FormNote = "";
        vm.CommitFormCommand.Execute(null);
    }

    // ---------- 一覧 ----------

    [Fact]
    public async Task Lists_programs_and_keep_separate_with_kind_and_counts()
    {
        var vm = await CreateLoaded(new FakeControl());

        Assert.True(vm.IsReady);
        Assert.True(vm.IsListMode);
        Assert.Equal(2, vm.Programs.Count);
        Assert.Equal("アルバム名だけ", vm.Programs[0].Kind); // W-204
        Assert.Equal("空白を入れて読みやすく", vm.Programs[0].Note);
        Assert.Equal("統合番組", vm.Programs[1].Kind);
        Assert.Equal("パンサー向井のふらっと(1)\nパンサー向井のふらっと(2)", vm.Programs[1].Folders);
        Assert.Equal("大竹まこと ゴールデンラジオ!(1) / 大竹まこと ゴールデンラジオ!(2)", Assert.Single(vm.KeepSeparate).Folders);
        Assert.Equal("2 件", vm.ProgramsCountText); // W-210
        Assert.Equal("1 件", vm.KeepCountText);
        Assert.False(vm.HasChanges);
        Assert.Null(vm.ChangesText);
        Assert.False(vm.ConfirmCommand.CanExecute(null)); // 変更が無いと押せない(W-207)
    }

    [Fact]
    public async Task Showing_page_again_rereads_file_only_without_unsaved_changes()
    {
        var vm = await CreateLoaded(new FakeControl());

        File.WriteAllText(ProgramsPath, Sample.Replace("パンサー向井のふらっと\" }", "パンサー向井のふらっと!\" }"));
        await vm.OnShownAsync(); // エディタで直した後にページを出し直した
        Assert.Equal("パンサー向井のふらっと!", vm.Programs[1].Album);

        AddAnzu(vm);
        File.WriteAllText(ProgramsPath, Sample);
        await vm.OnShownAsync();
        Assert.Equal(3, vm.Programs.Count); // 画面での変更は捨てない
    }

    [Fact]
    public async Task Missing_file_is_an_empty_list()
    {
        var vm = await CreateLoaded(new FakeControl(), content: null);

        Assert.True(vm.IsReady);
        Assert.Empty(vm.Programs);
        Assert.Equal("まだありません", vm.ProgramsCountText); // W-210
    }

    [Fact]
    public async Task Config_unreadable_W211()
    {
        var vm = new ProgramsPageViewModel(null, locateProgramsFile: null, _gate, _log.Add);
        await vm.LoadAsync();

        Assert.True(vm.IsUnavailable);
        Assert.False(vm.IsReady);
        Assert.Equal("設定を読めないため、番組の設定を出せません", ProgramsPageViewModel.Unavailable);
    }

    [Fact]
    public async Task Programs_file_unused_W212()
    {
        var vm = new ProgramsPageViewModel(new FakeControl(), () => null, _gate, _log.Add);
        await vm.LoadAsync();

        Assert.True(vm.IsUnused);
        Assert.False(vm.IsReady);
        Assert.False(vm.IsUnavailable);
    }

    [Fact]
    public async Task Broken_file_shows_reason_and_can_reload()
    {
        var vm = await CreateLoaded(new FakeControl(), content: "{ \"Programs\": [ ");

        Assert.True(vm.IsLoadFailed);
        Assert.Contains("JSON が壊れています", vm.LoadError);
        Assert.False(vm.IsReady);

        File.WriteAllText(ProgramsPath, Sample);
        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.False(vm.IsLoadFailed);
        Assert.Equal(2, vm.Programs.Count);
    }

    // ---------- 読んでいるあいだに始めた作業(#185) ----------

    // 読み込みを、gate が開くまで止めておける番組ページ(最初の読み込みは止めない)。止めた読み込みは、読み始めたときの
    // 中身(読めなかったことも)を返す(遅い NAS と同じく、読んでいるあいだに書かれても、書かれる前の中身になる)。
    private async Task<(ProgramsPageViewModel Vm, Func<TaskCompletionSource> HoldNextLoads)> CreateLoadedWithLoadGate(
        FakeControl? control = null)
    {
        File.WriteAllText(ProgramsPath, Sample, new UTF8Encoding(false));
        TaskCompletionSource? gate = null;
        var vm = new ProgramsPageViewModel(control ?? new FakeControl(), () => ProgramsPath, _gate, _log.Add, _log.Add,
            now: () => Now,
            readForLoad: async path =>
            {
                ProgramsFileSnapshot? read = null;
                Exception? failed = null;
                try
                {
                    read = ProgramsFileWriter.Read(path);
                }
                catch (Exception ex)
                {
                    failed = ex;
                }
                if (gate is not null)
                    await gate.Task;
                if (failed is not null)
                    ExceptionDispatchInfo.Throw(failed);
                return read!;
            });
        await vm.LoadAsync();
        return (vm, () => gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    [Fact]
    public void Editing_started_while_loading_is_kept() => UiThreadContext.Run(async () =>
    {
        var (vm, holdNextLoads) = await CreateLoadedWithLoadGate();
        var gate = holdNextLoads();
        File.WriteAllText(ProgramsPath, Sample.Replace("パンサー向井のふらっと\" }", "パンサー向井のふらっと!\" }"));

        var shown = vm.OnShownAsync(); // 一覧で変更が無いので読み直す
        Assert.False(shown.IsCompleted); // 読んでいる途中
        AddAnzu(vm); // 読んでいるあいだも一覧は出ているので、編集を始められる
        gate.SetResult();
        await shown;

        Assert.Equal(3, vm.Programs.Count); // 読み終えても、始めた編集を消さない
        Assert.True(vm.HasChanges);
        Assert.Equal("パンサー向井のふらっと", vm.Programs[1].Album); // 読んだ結果は捨てた(前の一覧のまま)
        Assert.Contains(_log, l => l.Contains("作り直しませんでした"));
    });

    [Fact]
    public void Saving_while_loading_is_not_undone_by_the_older_read() => UiThreadContext.Run(async () =>
    {
        // 読んでいるあいだに「編集 → 確認 → 保存して適用」まで済むと、保存した後の読み直しが一覧を作り直し、結果の画面
        // (変更なし)になる。後から終わった読み込み(保存の前の中身)で作り直すと、保存した番組が消えたように見える。
        var control = new FakeControl();
        var (vm, holdNextLoads) = await CreateLoadedWithLoadGate(control);
        var gate = holdNextLoads();

        var shown = vm.OnShownAsync(); // 一覧で変更が無いので読み直す(保存の前の中身を読んで止まる)
        Assert.False(shown.IsCompleted);
        AddAnzu(vm);
        await vm.ConfirmCommand.ExecuteAsync(null);
        await vm.SaveAndApplyCommand.ExecuteAsync(null);
        Assert.True(vm.IsResultMode);
        Assert.Equal(3, vm.Programs.Count);
        Assert.False(vm.HasChanges);
        gate.SetResult(); // 保存の前の中身を読み終わる
        await shown;

        Assert.True(vm.IsResultMode); // 結果を出したまま
        Assert.Equal(3, vm.Programs.Count); // 保存した番組を消さない
        Assert.Contains(_log, l => l.Contains("読んだ結果を捨てました"));

        // 次の保存の基準も、保存した後の中身のまま(古い中身に戻ると、W-275 を誤って出す)。
        await vm.BackCommand.ExecuteAsync(null);
        vm.SelectedProgram = vm.Programs[2];
        vm.DeleteProgramCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        await vm.SaveOnlyCommand.ExecuteAsync(null);
        Assert.False(vm.IsConflict);
        Assert.Equal(2, ProgramsFileWriter.Read(ProgramsPath).Config.Programs.Count);
    });

    [Fact]
    public void Form_opened_while_loading_stays_open() => UiThreadContext.Run(async () =>
    {
        var (vm, holdNextLoads) = await CreateLoadedWithLoadGate();
        var gate = holdNextLoads();

        var shown = vm.OnShownAsync();
        vm.AddProgramCommand.Execute(null); // フォームを開いたまま(一覧の画面ではない)
        vm.FormAlbum = "書きかけ";
        gate.SetResult();
        await shown;

        Assert.True(vm.IsProgramFormMode); // 一覧へ戻さない
        Assert.Equal("書きかけ", vm.FormAlbum);
    });

    [Fact]
    public void Load_failing_while_editing_keeps_the_edit() => UiThreadContext.Run(async () =>
    {
        var (vm, holdNextLoads) = await CreateLoadedWithLoadGate();
        var gate = holdNextLoads();
        File.WriteAllText(ProgramsPath, "{ \"Programs\": [ "); // 読んでいるあいだに壊れた

        var shown = vm.OnShownAsync();
        AddAnzu(vm);
        gate.SetResult();
        await shown;

        Assert.False(vm.IsLoadFailed); // 読めなかったことで一覧を消さない(gui.log には残す)
        Assert.Equal(3, vm.Programs.Count);
        Assert.Contains(_log, l => l.Contains("を読めませんでした"));
    });

    [Fact]
    public void Reload_W276_discards_changes_even_after_waiting_for_the_read() => UiThreadContext.Run(async () =>
    {
        // 「読み込み直す」(W-276)は、利用者が変更を捨てると決めて読み直す。読み終えたときに変更が残っていても作り直す。
        var (vm, holdNextLoads) = await CreateLoadedWithLoadGate();
        AddAnzu(vm);
        await vm.ConfirmCommand.ExecuteAsync(null);
        File.WriteAllText(ProgramsPath, Sample + "\n"); // エディタで直した
        await vm.SaveAndApplyCommand.ExecuteAsync(null);
        Assert.True(vm.IsConflict); // W-275
        var gate = holdNextLoads();

        var reload = vm.ReloadCommand.ExecuteAsync(null);
        Assert.False(reload.IsCompleted); // 読んでいる途中(変更はまだ残っている)
        Assert.True(vm.HasChanges);
        gate.SetResult();
        await reload;

        Assert.True(vm.IsListMode);
        Assert.False(vm.IsConflict);
        Assert.False(vm.HasChanges); // この画面での変更は消える
        Assert.Equal(2, vm.Programs.Count);
    });

    [Fact]
    public void Reload_W276_joining_a_running_load_discards_changes() => UiThreadContext.Run(async () =>
    {
        // 「読み込み直す」を押したとき、ほかの経路(ページを出し直した等)が始めた読み込みがすでに走っていれば、新しく始めずに
        // それに合流する。そのときも、読み終えたら変更を捨てて作り直すことを、その読み込みに伝える。
        var (vm, holdNextLoads) = await CreateLoadedWithLoadGate();
        var gate = holdNextLoads();
        var shown = vm.OnShownAsync(); // 一覧で変更が無いので、読み直しを始める(止めておく)
        Assert.False(shown.IsCompleted);
        AddAnzu(vm); // 読んでいるあいだに編集した(このままなら、読み終えても消さない)
        Assert.True(vm.HasChanges);

        var reload = vm.ReloadCommand.ExecuteAsync(null); // 走っている読み込みに合流する
        Assert.False(reload.IsCompleted);
        gate.SetResult();
        await shown;
        await reload;

        Assert.True(vm.IsListMode);
        Assert.False(vm.HasChanges); // 利用者が捨てると決めたので、作り直した
        Assert.Equal(2, vm.Programs.Count);
    });

    // ---------- 追加・編集・削除 ----------

    [Fact]
    public async Task Adding_accumulates_change_without_writing_and_revert_restores()
    {
        var vm = await CreateLoaded(new FakeControl());
        var before = File.ReadAllText(ProgramsPath);

        AddAnzu(vm);

        Assert.True(vm.IsListMode);
        Assert.Equal(3, vm.Programs.Count);
        var added = vm.Programs[^1]; // 末尾に足す(並べ替えは作らない)
        Assert.True(added.IsChanged);
        Assert.Equal(["安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)"], added.Current.Match.Folders);
        Assert.Null(added.Current.Note);
        Assert.Equal("保存していない変更があります(1 件)", vm.ChangesText); // W-208
        Assert.Equal(before, File.ReadAllText(ProgramsPath)); // ファイルは変わらない(W-201)

        vm.RevertCommand.Execute(null); // W-209

        Assert.Equal(2, vm.Programs.Count);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task Editing_and_deleting_count_as_changes_and_editing_back_is_no_change()
    {
        var vm = await CreateLoaded(new FakeControl());

        vm.SelectedProgram = vm.Programs[0];
        vm.EditProgramCommand.Execute(null);
        Assert.Equal("アルバム名と統合を編集", vm.FormHeading); // W-220
        Assert.Equal("LOGISTEED RADIONOMICS", vm.FormAlbum);
        vm.FormAlbum = "LOGISTEED RADIONOMICS!";
        vm.CommitFormCommand.Execute(null);
        Assert.Equal(1, vm.ChangeCount);

        vm.SelectedKeep = vm.KeepSeparate[0];
        vm.DeleteKeepCommand.Execute(null);
        Assert.Equal(2, vm.ChangeCount);

        // 元の値に戻せば変更ではなくなる
        vm.SelectedProgram = vm.Programs[0];
        vm.EditProgramCommand.Execute(null);
        vm.FormAlbum = "LOGISTEED RADIONOMICS";
        vm.CommitFormCommand.Execute(null);
        Assert.Equal(1, vm.ChangeCount);
    }

    [Fact]
    public async Task Form_validation_W224()
    {
        var vm = await CreateLoaded(new FakeControl());

        vm.AddProgramCommand.Execute(null);
        vm.FormFolders = "\n  \n";
        vm.CommitFormCommand.Execute(null);

        Assert.True(vm.IsProgramFormMode); // 決定できない
        Assert.Equal(["放送局を入れてください", "フォルダを 1 つ以上入れてください", "アルバム名を入れてください"], vm.FormErrors);

        vm.CancelFormCommand.Execute(null);
        Assert.True(vm.IsListMode);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task Same_folder_in_another_entry_of_same_station_W225()
    {
        var vm = await CreateLoaded(new FakeControl());

        vm.AddProgramCommand.Execute(null);
        vm.FormStation = "J-WAVE";
        // 取り込みと同じ照合キーで比べる(全角・半角の違いは同じフォルダ)
        vm.FormFolders = "ＬＯＧＩＳＴＥＥＤＲＡＤＩＯＮＯＭＩＣＳ\nほかのフォルダ";
        vm.FormAlbum = "別の名前";
        vm.CommitFormCommand.Execute(null);

        Assert.True(vm.IsProgramFormMode);
        Assert.Equal(["フォルダ『ＬＯＧＩＳＴＥＥＤＲＡＤＩＯＮＯＭＩＣＳ』は『LOGISTEED RADIONOMICS』の設定にも入っています"], vm.FormErrors);

        // 局が違えば別のフォルダ
        vm.FormStation = "TBSラジオ";
        vm.CommitFormCommand.Execute(null);
        Assert.True(vm.IsListMode);

        // 編集している行そのものとは比べない
        vm.SelectedProgram = vm.Programs[1];
        vm.EditProgramCommand.Execute(null);
        vm.CommitFormCommand.Execute(null);
        Assert.True(vm.IsListMode);
    }

    [Fact]
    public async Task Keep_separate_form_adds_and_edits()
    {
        var vm = await CreateLoaded(new FakeControl());

        vm.AddKeepCommand.Execute(null);
        Assert.Equal("分けておく兄弟フォルダを追加", vm.FormHeading); // W-229
        vm.FormStation = "文化放送";
        vm.FormFolders = "A(1)\nA(2)";
        vm.FormNote = "  別に聞く  ";
        vm.CommitFormCommand.Execute(null);

        var added = vm.KeepSeparate[^1];
        Assert.Equal("A(1) / A(2)", added.Folders);
        Assert.Equal("別に聞く", added.Note);

        vm.SelectedKeep = vm.KeepSeparate[0];
        vm.EditKeepCommand.Execute(null);
        Assert.Equal("分けておく兄弟フォルダを編集", vm.FormHeading); // W-228
        Assert.Equal("大竹まこと ゴールデンラジオ!(1)\n大竹まこと ゴールデンラジオ!(2)", vm.FormFolders);
    }

    // ---------- 確認 ----------

    [Fact]
    public async Task Confirm_runs_dry_run_with_unsaved_draft_and_shows_diff()
    {
        var control = new FakeControl();
        var vm = await CreateLoaded(control);
        var before = File.ReadAllText(ProgramsPath);
        AddAnzu(vm);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.True(vm.IsConfirmMode);
        // 保存前の設定(追加した 3 件目を含む)で確かめる。ファイルはまだ書かない
        Assert.Equal(3, control.PlannedWith!.Programs.Count);
        Assert.Single(control.PlannedWith.KeepSeparate);
        Assert.Equal(before, File.ReadAllText(ProgramsPath));

        Assert.Equal("2 回を編集した設定に揃えます", vm.ConfirmHeading); // W-242
        Assert.StartsWith("まだ保存していない設定で、", vm.ConfirmBody); // W-243
        Assert.Equal("「保存だけ」にすると、揃っていない 2 回は要対応の「再適用が要る番組」に残ります", vm.SaveOnlyNote); // W-250
        Assert.Equal("(なし)", vm.DiffBefore); // W-241
        Assert.Contains("\"Album\": \"安住紳一郎の日曜天国\"", vm.DiffAfter);
        Assert.Contains("安住紳一郎の日曜天国(2)", vm.DiffAfter); // 日本語はエスケープしない
        Assert.True(vm.IsConfirmBackDefault); // 既定は「戻る」
    }

    [Fact]
    public async Task Diff_of_deleted_entry_is_before_only()
    {
        var vm = await CreateLoaded(new FakeControl());
        vm.SelectedProgram = vm.Programs[0];
        vm.DeleteProgramCommand.Execute(null);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("LOGISTEEDRADIONOMICS", vm.DiffBefore);
        Assert.Contains("\"Note\": \"空白を入れて読みやすく\"", vm.DiffBefore);
        Assert.Equal("(なし)", vm.DiffAfter);
    }

    [Fact]
    public async Task Aligned_plan_W248_has_no_save_only_note()
    {
        var control = new FakeControl { Plan = Report(dryRun: true) };
        var vm = await CreateLoaded(control);
        AddAnzu(vm);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("取り込み済みの回は編集した設定と揃っています", vm.ConfirmHeading);
        Assert.Null(vm.SaveOnlyNote);
    }

    [Fact]
    public async Task Confirm_disabled_while_worker_stopped_W214()
    {
        var control = new FakeControl { Status = Snapshot(running: false) };
        var vm = await CreateLoaded(control);
        AddAnzu(vm);

        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.Equal("取り込みが止まっているため、確認と保存はできません", vm.ConfirmNote);

        control.Status = Snapshot();
        vm.RefreshStatus(); // 管理画面のタイマー

        Assert.True(vm.ConfirmCommand.CanExecute(null));
        Assert.Null(vm.ConfirmNote);
    }

    [Fact]
    public async Task Plan_failure_W251_and_back_keeps_draft()
    {
        var control = new FakeControl { PlanResult = () => ReapplyResult.Failed("設定の読み込みに失敗しました: x") };
        var vm = await CreateLoaded(control);
        AddAnzu(vm);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.True(vm.IsResultMode);
        Assert.Equal("統合設定の再適用を中止しました", vm.ResultHeading);
        Assert.Equal("設定の読み込みに失敗しました: x", vm.ResultBody);
        Assert.False(_gate.IsBusy);

        await vm.BackCommand.ExecuteAsync(null);
        Assert.True(vm.IsListMode);
        Assert.Equal(1, vm.ChangeCount);
    }

    // ---------- 保存 ----------

    [Fact]
    public async Task Save_and_apply_writes_then_reloads_and_applies_in_one_job()
    {
        var control = new FakeControl();
        var vm = await CreateLoaded(control);
        AddAnzu(vm);
        await vm.ConfirmCommand.ExecuteAsync(null);

        await vm.SaveAndApplyCommand.ExecuteAsync(null);

        // 保存 → 設定リロード → 再適用の実行(ADR-0016)。リロードと実行は worker の 1 つのジョブで(単独のリロードは要求しない)
        Assert.Equal(["plan", "reload-and-apply"], control.Calls);
        Assert.Same(control.Plan, control.AppliedPlan);

        var saved = ConfigLoader.Parse<ProgramMasterConfig>(File.ReadAllText(ProgramsPath));
        Assert.Equal(3, saved.Programs.Count);
        Assert.Equal("空白を入れて読みやすく", saved.Programs[0].Note);
        Assert.True(File.Exists(Path.Combine(_dir, "programs.json.bak-20260925-104512")));

        Assert.True(vm.IsResultMode);
        Assert.Equal("programs.json を保存しました(バックアップ: programs.json.bak-20260925-104512)", vm.SavedMessage); // W-270
        Assert.True(vm.IsSavedMessageVisible);
        Assert.Equal("統合設定の再適用が完了しました", vm.ResultHeading); // W-272
        Assert.False(vm.ResultIsError);

        // 保存した中身で一覧を作り直す(変更は無くなる)
        await vm.BackCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Programs.Count);
        Assert.False(vm.HasChanges);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_only_writes_and_reloads_without_applying_W279()
    {
        var control = new FakeControl();
        var vm = await CreateLoaded(control);
        AddAnzu(vm);
        await vm.ConfirmCommand.ExecuteAsync(null);

        await vm.SaveOnlyCommand.ExecuteAsync(null);

        Assert.Equal(["plan", "reload"], control.Calls);
        Assert.Equal(3, ConfigLoader.Parse<ProgramMasterConfig>(File.ReadAllText(ProgramsPath)).Programs.Count);
        Assert.Equal("保存しました。揃っていない回は要対応の「再適用が要る番組」に出ます", vm.ResultHeading);
        Assert.False(vm.IsSavedMessageVisible); // 「保存だけ」は 1 行で出す
    }

    [Fact]
    public async Task First_save_with_comments_asks_once_W260()
    {
        var control = new FakeControl();
        var vm = await CreateLoaded(control, "// 見本のコメント\n" + Sample.Replace("\"KeepSeparate\"", "/* 分けておく */ \"KeepSeparate\""));
        AddAnzu(vm);
        await vm.ConfirmCommand.ExecuteAsync(null);

        await vm.SaveAndApplyCommand.ExecuteAsync(null);

        Assert.True(vm.IsCommentsPromptVisible);
        Assert.Equal("コメントが 2 件消えます(.bak に残ります)", vm.CommentsHeading); // W-260
        Assert.Contains("同じフォルダに programs.json.bak-日時 を作ります", vm.CommentsBody); // W-261
        Assert.False(vm.IsConfirmBackDefault); // 既定は W-262 の「戻る」
        Assert.Equal(["plan"], control.Calls); // まだ書かない

        vm.CancelCommentsCommand.Execute(null); // W-262「戻る」
        Assert.False(vm.IsCommentsPromptVisible);
        Assert.True(vm.IsConfirmMode);

        await vm.SaveAndApplyCommand.ExecuteAsync(null);
        await vm.ProceedAfterCommentsCommand.ExecuteAsync(null); // W-262「保存する」

        Assert.Equal(["plan", "reload-and-apply"], control.Calls);
        Assert.DoesNotContain("//", File.ReadAllText(ProgramsPath));
    }

    [Fact]
    public async Task File_changed_after_load_is_not_written_W275()
    {
        var control = new FakeControl();
        var vm = await CreateLoaded(control);
        AddAnzu(vm);
        await vm.ConfirmCommand.ExecuteAsync(null);
        File.WriteAllText(ProgramsPath, Sample + "\n"); // エディタで直した

        await vm.SaveAndApplyCommand.ExecuteAsync(null);

        Assert.True(vm.IsResultMode);
        Assert.True(vm.IsConflict);
        Assert.False(vm.CanReturnFromResult); // 読み込み直すしかない(W-276)
        Assert.Equal("programs.json は読み込んだ後に変更されています(エディタで直した等)。保存していません。", vm.ResultHeading);
        Assert.Equal(["plan"], control.Calls); // 実行もリロードもしない
        Assert.Equal(Sample + "\n", File.ReadAllText(ProgramsPath));

        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.True(vm.IsListMode);
        Assert.False(vm.IsConflict);
        Assert.False(vm.HasChanges); // この画面での変更は消える
    }

    [Fact]
    public async Task Stale_apply_W278_then_confirm_again_applies_without_writing()
    {
        var control = new FakeControl();
        control.ApplyResult = _ => ReapplyResult.Stale("確認のあとに状態が変わったため中止しました。");
        var vm = await CreateLoaded(control);
        AddAnzu(vm);
        await vm.ConfirmCommand.ExecuteAsync(null);

        await vm.SaveAndApplyCommand.ExecuteAsync(null);

        Assert.Equal("確認した後に設定が変わったため、実行していません。もう一度「確認」を押してください", vm.ResultHeading);
        Assert.True(vm.ResultIsError);
        Assert.NotNull(vm.SavedMessage); // 保存はできている

        // 保存は済んでいるので変更は無いが、再適用をやり直すために「確認」を押せる
        await vm.BackCommand.ExecuteAsync(null);
        Assert.False(vm.HasChanges);
        Assert.True(vm.ConfirmCommand.CanExecute(null));

        control.ApplyResult = null;
        control.Calls.Clear();
        var written = File.ReadAllBytes(ProgramsPath);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.False(vm.SaveOnlyCommand.CanExecute(null)); // 保存するものが無い
        await vm.SaveAndApplyCommand.ExecuteAsync(null);

        Assert.Equal(["plan", "reload-and-apply"], control.Calls); // 書かないが、リロードは試し直す
        Assert.Equal(written, File.ReadAllBytes(ProgramsPath));
        Assert.Equal("統合設定の再適用が完了しました", vm.ResultHeading);

        await vm.BackCommand.ExecuteAsync(null);
        Assert.False(vm.ConfirmCommand.CanExecute(null)); // 実行できたので、もう確認は要らない
    }

    [Fact]
    public async Task Reload_failure_aborts_W251_then_retry_reloads_again_without_writing()
    {
        var control = new FakeControl { ReloadError = "stations.json を読めません" };
        var vm = await CreateLoaded(control);
        AddAnzu(vm);
        await vm.ConfirmCommand.ExecuteAsync(null);

        await vm.SaveAndApplyCommand.ExecuteAsync(null);

        // 保存はできたが、リロードに失敗したので再適用は実行していない。理由と、前の設定で動いていることを画面に出す
        Assert.Equal(["plan", "reload-and-apply"], control.Calls);
        Assert.Null(control.AppliedPlan);
        Assert.Equal(3, ConfigLoader.Parse<ProgramMasterConfig>(File.ReadAllText(ProgramsPath)).Programs.Count);
        Assert.True(vm.IsResultMode);
        Assert.Equal("統合設定の再適用を中止しました", vm.ResultHeading); // W-251
        Assert.Equal("設定の反映に失敗しました: stations.json を読めません(取り込みは前の設定のまま動いています)", vm.ResultBody);
        Assert.True(vm.ResultIsError);
        Assert.False(_gate.IsBusy);

        // 設定を変えずに(直したのは stations.json だけ)、もう一度実行できる。リロードも試し直す
        await vm.BackCommand.ExecuteAsync(null);
        Assert.False(vm.HasChanges);
        Assert.True(vm.ConfirmCommand.CanExecute(null));

        control.ReloadError = null; // 原因を直した。worker の側には古いリロードの失敗がまだ残っている
        Assert.NotNull(control.LastReloadError);
        control.Calls.Clear();
        var written = File.ReadAllBytes(ProgramsPath);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirmMode); // 古い失敗で「確認」が止まらない(#154 のレビュー 2 周目)
        await vm.SaveAndApplyCommand.ExecuteAsync(null);

        Assert.Equal(["plan", "reload-and-apply"], control.Calls);
        Assert.Null(control.LastReloadError); // 実行の前にリロードを試し直し、成功した
        Assert.Equal(written, File.ReadAllBytes(ProgramsPath)); // 書かない
        Assert.Equal("統合設定の再適用が完了しました", vm.ResultHeading);
    }

    [Fact]
    public async Task Pending_reapply_is_kept_when_file_unchanged_and_reread_when_edited()
    {
        var control = new FakeControl { ReloadError = "x" };
        var vm = await CreateLoaded(control);
        AddAnzu(vm);
        await vm.ConfirmCommand.ExecuteAsync(null);
        await vm.SaveAndApplyCommand.ExecuteAsync(null);
        await vm.BackCommand.ExecuteAsync(null);
        Assert.True(vm.ConfirmCommand.CanExecute(null)); // 中止された再適用を続けられる

        await vm.OnShownAsync(); // ページを出し直しても、ファイルが同じなら読み直さない
        Assert.True(vm.ConfirmCommand.CanExecute(null));
        Assert.Equal(3, vm.Programs.Count);

        // エディタで直したら読み直す(usage §6:エディタで直した後は、番組ページを開き直すと読み直す)
        var edited = File.ReadAllText(ProgramsPath).Replace("安住紳一郎の日曜天国\"", "安住紳一郎の日曜天国!\"");
        File.WriteAllText(ProgramsPath, edited);
        await vm.OnShownAsync();

        Assert.Equal("安住紳一郎の日曜天国!", vm.Programs[2].Album);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task Write_failure_W277_keeps_file_and_draft()
    {
        // フォルダを読み取り専用にして、バックアップ(一時ファイル)を作れなくする。Unix のアクセス権で作るので
        // Windows では確かめない(CI は Linux)。
        if (OperatingSystem.IsWindows())
            return;

        var control = new FakeControl();
        var vm = await CreateLoaded(control);
        AddAnzu(vm);
        await vm.ConfirmCommand.ExecuteAsync(null);
        var mode = File.GetUnixFileMode(_dir);
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await vm.SaveAndApplyCommand.ExecuteAsync(null);
        }
        finally
        {
            File.SetUnixFileMode(_dir, mode);
        }

        Assert.StartsWith("書き込みに失敗しました: ", vm.ResultHeading);
        Assert.Equal("programs.json は元のままです。", vm.ResultBody);
        Assert.Equal(Sample, File.ReadAllText(ProgramsPath));
        Assert.Equal(["plan"], control.Calls);

        await vm.BackCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.ChangeCount); // もう一度やり直せる
    }

    // ---------- 再スキャンのガード(W-110)----------

    [Fact]
    public async Task Home_rescan_is_disabled_from_confirm_until_back_to_list()
    {
        var control = new FakeControl();
        File.WriteAllText(ProgramsPath, Sample);
        var vm = new ManagementViewModel(control, null, null, new NoActions(), _log.Add, Jst,
            locateProgramsFile: () => ProgramsPath, now: () => Now);
        await vm.Programs.OnShownAsync(); // 番組ページを出したら読む
        Assert.True(vm.Programs.IsReady);
        Assert.True(vm.Home.RescanCommand.CanExecute(null));

        AddAnzu(vm.Programs);
        await vm.Programs.ConfirmCommand.ExecuteAsync(null);

        Assert.False(vm.Home.CanRescan);
        Assert.False(vm.Home.RescanCommand.CanExecute(null));
        vm.Home.Refresh(); // 2 秒ごとの読み直しでも戻らない
        Assert.False(vm.Home.CanRescan);

        await vm.Programs.BackCommand.ExecuteAsync(null);

        Assert.True(vm.Home.CanRescan);
    }

    [Fact]
    public async Task Rescan_is_back_after_save_and_apply_completes()
    {
        var control = new FakeControl();
        File.WriteAllText(ProgramsPath, Sample);
        var vm = new ManagementViewModel(control, null, null, new NoActions(), _log.Add, Jst,
            locateProgramsFile: () => ProgramsPath, now: () => Now);
        await vm.Programs.LoadAsync();
        AddAnzu(vm.Programs);
        await vm.Programs.ConfirmCommand.ExecuteAsync(null);

        await vm.Programs.SaveAndApplyCommand.ExecuteAsync(null);

        Assert.True(vm.Programs.IsResultMode);
        Assert.True(vm.Home.CanRescan);
    }

    private sealed class NoActions : IHistoryPathActions
    {
        public Task<string> RevealAsync(string path) => Task.FromResult("");
        public bool TryCopy(string path) => true;
    }
}
