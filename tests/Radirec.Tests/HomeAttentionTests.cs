using System.IO.Abstractions.TestingHelpers;
using System.Text;
using Radirec.Core.Attention;
using Radirec.Core.Configuration;
using Radirec.Core.Ledger;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;
using Radirec.Core.Sources;
using Radirec.Gui.Core;
using Radirec.Gui.Core.Management;
using Radirec.Hosting;
using Radirec.Hosting.Control;
using static Radirec.Tests.ServiceStatusTextTests;

namespace Radirec.Tests;

// 管理画面のホームの要対応「統合設定の無い兄弟フォルダ」(#155・ADR-0018)。文言の ID は docs/mockups/management-window.md。
// 要対応は、台帳の代わりの記録と、一時フォルダに置いた実物の programs.json から計算する(本番の AttentionLoader と同じ計算)。
public sealed class HomeAttentionTests : IDisposable
{
    private const string Tbs = "TBSラジオ";
    private const string Qr = "文化放送";

    private const string Sample = """
        {
          "Programs": [
            { "Match": { "Station": "TBSラジオ", "Folders": ["パンサー向井のふらっと(1)", "パンサー向井のふらっと(2)"] },
              "Canonical": { "Album": "パンサー向井のふらっと" } }
          ],
          "KeepSeparate": [
            { "Match": { "Station": "文化放送", "Folders": ["大竹まこと ゴールデンラジオ!(1)", "大竹まこと ゴールデンラジオ!(2)"] },
              "Note": "曜日ごとに別の番組" }
          ]
        }
        """;

    private sealed class FakeControl : IServiceControl
    {
        public ServiceStatusSnapshot Status { get; set; } = Snapshot(completed: DateTimeOffset.UnixEpoch);
        public void RequestRescan() { }
        public Task<ScanRunResult> RunScanAsync() => throw new NotSupportedException();
        public ServiceStatusSnapshot GetStatus() => Status;
        public ReloadResult RequestReload() => ReloadResult.Ok();
        // 保存済みの設定での確認(#164)→「適用」で結果の画面まで進めるための予定(1 回を動かす)。
        public Task<ReapplyResult> PlanReapplyAsync() => Task.FromResult(ReapplyResult.Ok(MoveOne(dryRun: true)));
        // 番組ページの「確認」→「保存だけ」で結果の画面まで進めるための dry-run(揃っている予定)。
        public Task<ReapplyResult> PlanReapplyAsync(ProgramMasterConfig programs) => Task.FromResult(ReapplyResult.Ok(
            new ReapplyReport { Items = [], DryRun = true, UnintegratedSiblings = [] }));
        public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan) => Task.FromResult(ReapplyResult.Ok(MoveOne(dryRun: false)));
        public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
    }

    private static ReapplyReport MoveOne(bool dryRun) => new()
    {
        Items =
        [
            new ReapplyItem
            {
                Action = ReapplyAction.Move,
                Station = Tbs,
                FromAlbum = "安住紳一郎の日曜天国(1)",
                ToAlbum = "安住紳一郎の日曜天国",
                BroadcastDate = new DateOnly(2024, 9, 23),
                FromPath = "/out/a.m4a",
                ToPath = "/out/b.m4a",
                SourcePath = "/radio/a.m4a",
            },
        ],
        DryRun = dryRun,
        UnintegratedSiblings = [],
    };

    private sealed class NoPathActions : IHistoryPathActions
    {
        public Task<string> RevealAsync(string path) => Task.FromResult("");
        public bool TryCopy(string path) => true;
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("radirec-attention-").FullName;
    private readonly List<string> _log = [];
    private readonly List<LedgerEntry> _ledger = [];
    private readonly FakeControl _control = new();
    private int _computes;
    private int _locates; // programs.json の場所を決めた回数(= 番組ページの読み込みと、「分けておく」の読み込みの回数)

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ProgramsPath => Path.Combine(_dir, "programs.json");

    private void Ingested(string station, params string[] folders)
    {
        foreach (var folder in folders)
        {
            _ledger.Add(new LedgerEntry
            {
                IdentityKey = $"{station}|{folder}",
                Station = station,
                Program = folder,
                BroadcastDate = new DateOnly(2024, 9, 23),
                SourcePath = $"/radio/{station}/{folder}/[{station}]{folder}(TimeFree)_20240923083000.m4a",
                OutputPath = $"/out/{station}/{folder}/{folder} 2024-09-23.m4a",
                RuleVersion = "1",
                UsedFallback = true,
                ImportedAtUtc = DateTimeOffset.UnixEpoch,
            });
        }
    }

    private AttentionItems Compute()
    {
        _computes++;
        var programs = ProgramsFileWriter.Read(ProgramsPath).Config;
        var resolver = new LedgerEpisodeResolver(
            new MockFileSystem().Path, new RajirekoImporter(),
            new StationResolver(new StationMasterConfig()), new ProgramResolver(programs));
        return AttentionCalculator.Compute(_ledger, resolver, new AttentionPrograms(programs));
    }

    private ManagementViewModel CreateNow(string? content = Sample, Func<AttentionItems>? compute = null, bool degraded = false)
    {
        if (content is not null)
            File.WriteAllText(ProgramsPath, content, new UTF8Encoding(false));
        return new ManagementViewModel(
            _control, null, () => [], new NoPathActions(), _log.Add,
            locateProgramsFile: () => { _locates++; return ProgramsPath; }, now: () => new DateTime(2026, 9, 25, 12, 0, 0),
            computeAttention: degraded ? null : compute ?? Compute);
    }

    // 要対応は UI スレッドの外で計算するので、開いたときの計算が終わるのを待つ。
    private async Task<ManagementViewModel> Create(string? content = Sample, Func<AttentionItems>? compute = null, bool degraded = false)
    {
        var vm = CreateNow(content, compute, degraded);
        await vm.Home.AttentionRefreshed;
        return vm;
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    // ---------- 一覧 ----------

    [Fact]
    public async Task Lists_sibling_groups_with_count_W120_W123()
    {
        Ingested(Tbs, "安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)");

        var home = (await Create()).Home;

        var row = Assert.Single(home.SiblingRows);
        Assert.Equal("要対応 1 件", home.AttentionCountText); // W-120
        Assert.Equal("TBSラジオ: 安住紳一郎の日曜天国(1)(設定なし) / 安住紳一郎の日曜天国(2)(設定なし)", row.Line); // W-123
        Assert.Null(row.Heading);
        Assert.False(home.IsAttentionEmpty);
        Assert.Null(home.AttentionError);
    }

    [Fact]
    public async Task Nothing_to_do_W121()
    {
        // 兄弟フォルダの無い番組で、記録どおりに取り込んだ回だけ(3 種のどれも無い)。
        Ingested(Tbs, "単発の番組");

        var home = (await Create()).Home;

        Assert.Empty(home.SiblingRows);
        Assert.Empty(home.DriftRows);
        Assert.True(home.IsAttentionEmpty);
        Assert.Null(home.AttentionCountText);
        Assert.Equal("要対応はありません", AttentionText.None);
    }

    [Fact]
    public async Task Integrated_group_recorded_under_folder_names_is_not_a_sibling_but_needs_reapply_W134()
    {
        // 統合済みの組を、統合する前のアルバム(フォルダ名)のまま記録した台帳。兄弟フォルダには出ず(統合済み)、
        // 「再適用が要る番組」に出る(#156)。
        Ingested(Tbs, "パンサー向井のふらっと(1)", "パンサー向井のふらっと(2)");

        var home = (await Create()).Home;

        Assert.Empty(home.SiblingRows);
        Assert.Equal(2, home.DriftRows.Count);
        Assert.All(home.DriftRows, row => Assert.Equal("パンサー向井のふらっと", row.Drift.ToAlbum));
        Assert.False(home.IsAttentionEmpty);
    }

    [Fact]
    public async Task Merged_and_kept_apart_groups_have_their_own_lines_W124_W125()
    {
        Ingested(Tbs, "パンサー向井のふらっと(1)", "パンサー向井のふらっと(2)", "パンサー向井のふらっと(3)");
        Ingested(Qr, "大竹まこと ゴールデンラジオ!(1)", "大竹まこと ゴールデンラジオ!(2)", "大竹まこと ゴールデンラジオ!(3)");

        var rows = (await Create()).Home.SiblingRows;

        Assert.Equal(2, rows.Count); // 放送局の序数順
        Assert.Equal("統合番組『パンサー向井のふらっと』に入っていないフォルダ『パンサー向井のふらっと(3)』があります", rows[0].Heading); // W-124
        Assert.Equal("分けておく組に、新しいフォルダ『大竹まこと ゴールデンラジオ!(3)』が加わりました", rows[1].Heading); // W-125
    }

    [Fact]
    public async Task Degraded_W137()
    {
        var home = (await Create(degraded: true)).Home;

        Assert.Equal("設定を読めないため、要対応を出せません", home.AttentionError);
        Assert.False(home.IsAttentionEmpty);
        Assert.Empty(home.SiblingRows);
    }

    [Fact]
    public async Task Compute_failure_shows_reason_W137()
    {
        var home = (await Create(compute: () => throw new IOException("台帳を開けません"))).Home;

        Assert.Equal("要対応を出せませんでした: 台帳を開けません", home.AttentionError);
        Assert.False(home.IsAttentionEmpty);
        Assert.Contains(_log, l => l.Contains("要対応を計算できませんでした"));
    }

    [Fact]
    public async Task Recomputes_only_when_scan_completes_or_home_is_shown()
    {
        var vm = await Create();
        Assert.Equal(1, _computes); // 開いたとき

        vm.Tick();
        vm.Tick();
        Assert.Equal(1, _computes); // 2 秒ごとには計算しない

        _control.Status = Snapshot(completed: DateTimeOffset.UnixEpoch.AddMinutes(1));
        vm.Tick();
        await vm.Home.AttentionRefreshed;
        Assert.Equal(2, _computes); // 最終スキャンが変わった

        vm.CurrentPage = ManagementPage.History;
        vm.CurrentPage = ManagementPage.Home;
        await vm.Home.AttentionRefreshed;
        Assert.Equal(3, _computes); // ホームへ切り替えた
    }

    [Fact]
    public async Task Computes_off_the_calling_thread_without_reentry()
    {
        var gate = new ManualResetEventSlim();
        var running = 0;
        var maxRunning = 0;
        var callerThread = Environment.CurrentManagedThreadId;
        var computeThreads = new List<int>();
        AttentionItems Blocking()
        {
            lock (computeThreads)
                computeThreads.Add(Environment.CurrentManagedThreadId);
            maxRunning = Math.Max(maxRunning, Interlocked.Increment(ref running));
            gate.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref running);
            return Compute();
        }

        var vm = CreateNow(compute: Blocking);
        Assert.Null(vm.Home.AttentionState); // 計算し終えるまでは「要対応はありません」を出さない
        Assert.False(vm.Home.IsAttentionEmpty);

        // 計算の途中に 2 回頼まれても、並べて走らせない(終わってから 1 度だけ計算し直す)。
        vm.Home.RefreshAttention();
        vm.Home.RefreshAttention();
        gate.Set();
        await vm.Home.AttentionRefreshed;

        Assert.Equal(1, maxRunning);
        Assert.Equal(2, _computes);
        Assert.NotEqual(callerThread, computeThreads[0]); // 呼んだスレッドがまだ動いている間に、別のスレッドで計算していた
        Assert.True(vm.Home.IsAttentionEmpty);
    }

    // ---------- 統合する ----------

    [Fact]
    public async Task Integrate_opens_program_form_with_new_entry_draft_W226()
    {
        Ingested(Tbs, "安住紳一郎の日曜天国(2)", "安住紳一郎の日曜天国(1)");
        var vm = await Create();
        var before = File.ReadAllText(ProgramsPath);

        vm.Home.SiblingRows[0].IntegrateCommand.Execute(null);
        await WaitUntil(() => vm.Programs.IsProgramFormMode);

        Assert.Equal(ManagementPage.Programs, vm.CurrentPage);
        Assert.Equal("アルバム名と統合を追加", vm.Programs.FormHeading);
        Assert.Equal(Tbs, vm.Programs.FormStation);
        Assert.Equal("安住紳一郎の日曜天国(1)\n安住紳一郎の日曜天国(2)", vm.Programs.FormFolders); // パートの順
        Assert.Equal("安住紳一郎の日曜天国", vm.Programs.FormAlbum);
        Assert.Equal(ProgramsPageViewModel.DraftFromAttention, vm.Programs.FormNotice); // W-226
        Assert.Equal(before, File.ReadAllText(ProgramsPath)); // 書かない

        vm.Programs.CommitFormCommand.Execute(null);
        Assert.True(vm.Programs.HasChanges); // 決定で一覧の変更になる(保存は利用者が「確認」から)
        Assert.Equal("安住紳一郎の日曜天国", vm.Programs.Programs[^1].Album);
    }

    [Fact]
    public async Task Integrate_group_partly_merged_appends_folder_to_the_merged_program()
    {
        Ingested(Tbs, "パンサー向井のふらっと(1)", "パンサー向井のふらっと(2)", "パンサー向井のふらっと(3)");
        var vm = await Create();

        vm.Home.SiblingRows[0].IntegrateCommand.Execute(null);
        await WaitUntil(() => vm.Programs.IsProgramFormMode);

        Assert.Equal("アルバム名と統合を編集", vm.Programs.FormHeading);
        Assert.Equal("パンサー向井のふらっと(1)\nパンサー向井のふらっと(2)\nパンサー向井のふらっと(3)", vm.Programs.FormFolders);
        Assert.Equal("パンサー向井のふらっと", vm.Programs.FormAlbum);

        vm.Programs.CommitFormCommand.Execute(null);
        var row = Assert.Single(vm.Programs.Programs);
        Assert.Equal(3, row.Current.Match.Folders.Count); // 既存のエントリを書き換える(2 つ目を作らない)
    }

    // ---------- 分けておく ----------

    [Fact]
    public async Task Keep_apart_writes_programs_json_and_the_group_disappears()
    {
        Ingested(Tbs, "安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)");
        var vm = await Create();

        await vm.Home.SiblingRows[0].KeepApartCommand.ExecuteAsync(null);

        var kept = ProgramsFileWriter.Read(ProgramsPath).Config.KeepSeparate;
        Assert.Equal(2, kept.Count);
        Assert.Equal(["安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)"], kept[1].Match.Folders);
        Assert.Single(Directory.GetFiles(_dir, "programs.json.bak-*")); // バックアップを作る
        Assert.Empty(vm.Home.SiblingRows); // 計算し直して消える
        Assert.True(vm.Home.IsAttentionEmpty);
        Assert.Null(vm.Home.AttentionNote);
        Assert.Equal(ManagementPage.Home, vm.CurrentPage); // 番組ページへは移らない
    }

    [Fact]
    public async Task Keep_apart_for_a_grown_group_replaces_the_kept_entry()
    {
        Ingested(Qr, "大竹まこと ゴールデンラジオ!(1)", "大竹まこと ゴールデンラジオ!(2)", "大竹まこと ゴールデンラジオ!(3)");
        var vm = await Create();

        await vm.Home.SiblingRows[0].KeepApartCommand.ExecuteAsync(null);

        var kept = Assert.Single(ProgramsFileWriter.Read(ProgramsPath).Config.KeepSeparate);
        Assert.Equal(
            ["大竹まこと ゴールデンラジオ!(1)", "大竹まこと ゴールデンラジオ!(2)", "大竹まこと ゴールデンラジオ!(3)"],
            kept.Match.Folders);
        Assert.Equal("曜日ごとに別の番組", kept.Note); // メモは残す
        Assert.Empty(vm.Home.SiblingRows);
    }

    // 番組ページで変更を「保存だけ」して、結果の画面のままにする。
    private static async Task SaveOnlyToResult(ManagementViewModel vm)
    {
        await vm.Programs.LoadAsync();
        vm.Programs.SelectedProgram = vm.Programs.Programs[0];
        vm.Programs.DeleteProgramCommand.Execute(null);
        await vm.Programs.ConfirmCommand.ExecuteAsync(null);
        await vm.Programs.SaveOnlyCommand.ExecuteAsync(null);
        Assert.True(vm.Programs.IsResultMode);
        Assert.False(vm.Programs.HasUnsavedWork); // 結果の画面は保存済み(「分けておく」を押せる)
    }

    [Fact]
    public async Task Programs_page_left_on_result_rereads_after_keep_apart()
    {
        Ingested(Tbs, "安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)");
        var vm = await Create();
        await SaveOnlyToResult(vm);

        vm.CurrentPage = ManagementPage.Home;
        await vm.Home.SiblingRows[0].KeepApartCommand.ExecuteAsync(null);
        vm.CurrentPage = ManagementPage.Programs; // 結果の画面のまま戻ってくる

        // 古い一覧のままだと、次の保存が W-275 で弾かれる。ファイルが変わったので読み直す。
        await WaitUntil(() => vm.Programs.IsListMode && vm.Programs.KeepSeparate.Count == 2);
        Assert.False(vm.Programs.HasChanges);
    }

    [Fact]
    public async Task Programs_page_left_on_result_keeps_result_when_file_is_unchanged()
    {
        var vm = await Create();
        await SaveOnlyToResult(vm);

        vm.CurrentPage = ManagementPage.Home;
        vm.CurrentPage = ManagementPage.Programs;
        await Task.Delay(50);

        Assert.True(vm.Programs.IsResultMode); // 変わっていなければ結果を出したまま
    }

    [Fact]
    public async Task Back_from_result_rereads_when_file_changed()
    {
        var vm = await Create();
        await SaveOnlyToResult(vm);
        File.WriteAllText(ProgramsPath, Sample); // 結果を出している間に、ほかの経路で書かれた

        await vm.Programs.BackCommand.ExecuteAsync(null);

        Assert.True(vm.Programs.IsListMode);
        Assert.Single(vm.Programs.Programs); // Sample の 1 件を読み直した(削除して保存した 0 件ではない)
        Assert.False(vm.Programs.HasChanges);
    }

    [Fact]
    public void Back_from_result_does_not_reread_when_editing_started_while_checking() => UiThreadContext.Run(async () =>
    {
        // UI スレッドと同じく、「戻る」がファイルの変更を確かめて待っている間に、利用者が一覧の編集を始められる。
        var vm = CreateNow();
        await SaveOnlyToResult(vm);
        File.WriteAllText(ProgramsPath, Sample); // 結果を出している間に、ほかの経路で書かれた

        var back = vm.Programs.BackCommand.ExecuteAsync(null);
        Assert.True(vm.Programs.IsListMode); // 一覧に戻り、ファイルを確かめている途中
        vm.Programs.AddProgramCommand.Execute(null);
        vm.Programs.FormStation = Tbs;
        vm.Programs.FormFolders = "安住紳一郎の日曜天国";
        vm.Programs.FormAlbum = "安住紳一郎の日曜天国";
        vm.Programs.CommitFormCommand.Execute(null);
        await back;

        Assert.True(vm.Programs.HasChanges); // 読み直して、始めた編集を黙って消さない
        Assert.Equal("安住紳一郎の日曜天国", Assert.Single(vm.Programs.Programs).Album);
    });

    [Fact]
    public void Back_from_result_does_not_reread_when_confirm_started_while_checking() => UiThreadContext.Run(async () =>
    {
        var vm = CreateNow();
        await SaveOnlyToResult(vm);
        File.WriteAllText(ProgramsPath, Sample);

        var back = vm.Programs.BackCommand.ExecuteAsync(null);
        vm.Programs.AddProgramCommand.Execute(null); // フォームを開いたまま(一覧の画面ではない)
        await back;

        Assert.True(vm.Programs.IsProgramFormMode); // 読み直して一覧へ戻さない
        Assert.Empty(vm.Programs.Programs);
    });

    // ---------- 結果の画面のまま、要対応から下書きを入れる(#199) ----------

    [Fact]
    public async Task Integrate_from_the_result_screen_opens_the_draft_W226()
    {
        Ingested(Tbs, "安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)");
        var vm = await Create();
        await SaveOnlyToResult(vm);

        vm.CurrentPage = ManagementPage.Home; // 結果の画面のままホームへ
        vm.Home.SiblingRows[0].IntegrateCommand.Execute(null);
        await WaitUntil(() => vm.Programs.IsProgramFormMode);

        Assert.Equal(ManagementPage.Programs, vm.CurrentPage);
        Assert.Equal("アルバム名と統合を追加", vm.Programs.FormHeading);
        Assert.Equal("安住紳一郎の日曜天国(1)\n安住紳一郎の日曜天国(2)", vm.Programs.FormFolders);
        Assert.Equal(ProgramsPageViewModel.DraftFromAttention, vm.Programs.FormNotice); // W-226
    }

    [Fact]
    public async Task Keep_apart_with_comments_from_the_saved_apply_result_opens_the_keep_draft()
    {
        // コメントが残ったまま結果の画面になるのは、保存済みの設定での確認(#164)から「適用」したとき(programs.json を書かない)。
        Ingested(Tbs, "安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)");
        var withComment = Sample.Replace("\"Programs\": [", "// 手で書いたメモ\n  \"Programs\": [");
        var vm = await Create(withComment);
        var start = await vm.Programs.ConfirmSavedAsync(ManagementPage.Settings);
        Assert.True(start.ShowsConfirm);
        await vm.Programs.ApplySavedCommand.ExecuteAsync(null);
        Assert.True(vm.Programs.IsResultMode);

        vm.CurrentPage = ManagementPage.Home; // 結果の画面のままホームへ
        await vm.Home.SiblingRows[0].KeepApartCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Programs.IsKeepFormMode);

        Assert.Equal(withComment, File.ReadAllText(ProgramsPath)); // その場では書かない(保存の前に W-260 で確かめる)
        Assert.Equal(ManagementPage.Programs, vm.CurrentPage);
        Assert.Equal(ProgramsPageViewModel.KeepDraftFromAttention, vm.Programs.FormNotice); // W-226 の 2 つ目
        Assert.False(vm.Programs.IsSavedBasis); // 保存済みの設定での確認は終えた(「戻る」で設定ページへ移らない)
    }

    // 入口から出口まで:「統合する」→ 番組ページに、読み直した一覧の上の下書き。この筋書きでは、ShowDraftAsync が先に呼ぶ
    // OnShownAsync が(結果の画面でファイルが変わっていれば)読み直して一覧に戻すので、LeaveResultForDraftAsync の読み直しの
    // 分岐は通らない。その分岐は、下の LeaveResultForDraftAsync を直接呼ぶテストで確かめる。
    [Fact]
    public async Task Integrate_end_to_end_after_the_file_changed_while_on_the_result_screen()
    {
        Ingested(Tbs, "安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)");
        var vm = await Create();
        await SaveOnlyToResult(vm);
        File.WriteAllText(ProgramsPath, Sample); // 結果を出している間に、ほかの経路で書かれた

        vm.CurrentPage = ManagementPage.Home;
        vm.Home.SiblingRows[0].IntegrateCommand.Execute(null);
        await WaitUntil(() => vm.Programs.IsProgramFormMode);

        Assert.Single(vm.Programs.Programs); // 削除して保存した 0 件ではなく、書き直されたファイルを読み直してから下書き
        Assert.False(vm.Programs.HasChanges);
    }

    [Fact]
    public async Task Leaving_the_result_screen_for_a_draft_rereads_when_the_file_changed()
    {
        var vm = await Create();
        await SaveOnlyToResult(vm);
        File.WriteAllText(ProgramsPath, Sample); // 結果を出している間に、ほかの経路で書かれた

        await vm.Programs.LeaveResultForDraftAsync();

        Assert.True(vm.Programs.IsListMode);
        Assert.Single(vm.Programs.Programs); // Sample の 1 件を読み直した(削除して保存した 0 件ではない)
        Assert.False(vm.Programs.HasChanges);
    }

    [Fact]
    public async Task Leaving_the_result_screen_for_a_draft_keeps_the_list_when_the_file_is_unchanged()
    {
        var vm = await Create();
        await SaveOnlyToResult(vm);
        var loads = _locates;

        await vm.Programs.LeaveResultForDraftAsync();

        Assert.True(vm.Programs.IsListMode);
        Assert.Empty(vm.Programs.Programs); // 保存した一覧のまま
        Assert.Equal(loads, _locates); // 読み直していない
    }

    // 保存済みの設定での確認(#164)から「適用」した結果の画面(programs.json を書かない)。
    private static async Task SavedApplyToResult(ManagementViewModel vm)
    {
        await vm.Programs.LoadAsync();
        var start = await vm.Programs.ConfirmSavedAsync(ManagementPage.Settings);
        Assert.True(start.ShowsConfirm);
        await vm.Programs.ApplySavedCommand.ExecuteAsync(null);
        Assert.True(vm.Programs.IsResultMode);
        Assert.True(vm.Programs.IsSavedBasis);
    }

    [Fact]
    public async Task Leaving_the_saved_apply_result_for_a_draft_rereads_when_the_file_changed()
    {
        var vm = await Create();
        await SavedApplyToResult(vm);
        File.WriteAllText(ProgramsPath, Sample.Replace("パンサー向井のふらっと\" }", "パンサー向井のふらっと(新)\" }"));
        var loads = _locates;
        var page = vm.CurrentPage;

        await vm.Programs.LeaveResultForDraftAsync();

        Assert.True(vm.Programs.IsListMode);
        Assert.False(vm.Programs.IsSavedBasis); // 確認を終えた
        Assert.Equal(page, vm.CurrentPage); // 来たページ(設定)へは移らない
        Assert.Equal(loads + 1, _locates); // 読み直した
        Assert.Equal("パンサー向井のふらっと(新)", Assert.Single(vm.Programs.Programs).Album);
    }

    [Fact]
    public async Task Leaving_the_saved_apply_result_for_a_draft_keeps_the_list_when_the_file_is_unchanged()
    {
        var vm = await Create();
        await SavedApplyToResult(vm);
        var loads = _locates;

        await vm.Programs.LeaveResultForDraftAsync();

        Assert.True(vm.Programs.IsListMode);
        Assert.False(vm.Programs.IsSavedBasis);
        Assert.Equal(loads, _locates); // 読み直していない
    }

    [Fact]
    public async Task Result_with_unsaved_changes_W275_is_left_as_is()
    {
        var vm = await Create();
        await vm.Programs.LoadAsync();
        vm.Programs.SelectedProgram = vm.Programs.Programs[0];
        vm.Programs.DeleteProgramCommand.Execute(null);
        await vm.Programs.ConfirmCommand.ExecuteAsync(null);
        File.WriteAllText(ProgramsPath, Sample + "\n"); // エディタで直した
        await vm.Programs.SaveOnlyCommand.ExecuteAsync(null);
        Assert.True(vm.Programs.IsConflict); // W-275:保存していない変更を持ったまま結果の画面

        await vm.Programs.LeaveResultForDraftAsync();

        Assert.True(vm.Programs.IsResultMode); // 読み込み直すかを利用者が決める画面のまま(下書きは作らない)
        Assert.True(vm.Programs.HasChanges);
    }

    [Fact]
    public async Task Integrate_while_editing_a_form_does_not_replace_it()
    {
        Ingested(Tbs, "安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)");
        var vm = await Create();
        await vm.Programs.LoadAsync();
        vm.Programs.AddProgramCommand.Execute(null);
        vm.Programs.FormAlbum = "書きかけ";

        vm.CurrentPage = ManagementPage.Home;
        vm.Home.SiblingRows[0].IntegrateCommand.Execute(null);
        await WaitUntil(() => vm.CurrentPage == ManagementPage.Programs);

        Assert.True(vm.Programs.IsProgramFormMode);
        Assert.Equal("書きかけ", vm.Programs.FormAlbum); // 編集の途中のフォームを下書きで上書きしない
        Assert.NotEqual(ProgramsPageViewModel.DraftFromAttention, vm.Programs.FormNotice);
    }

    [Fact]
    public async Task Integrate_during_confirm_does_not_leave_the_confirm_screen()
    {
        Ingested(Tbs, "安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)");
        var vm = await Create();
        await vm.Programs.LoadAsync();
        vm.Programs.SelectedProgram = vm.Programs.Programs[0];
        vm.Programs.DeleteProgramCommand.Execute(null);
        await vm.Programs.ConfirmCommand.ExecuteAsync(null);
        Assert.True(vm.Programs.IsConfirmMode);

        vm.CurrentPage = ManagementPage.Home;
        vm.Home.SiblingRows[0].IntegrateCommand.Execute(null);
        await WaitUntil(() => vm.CurrentPage == ManagementPage.Programs);

        Assert.True(vm.Programs.IsConfirmMode); // 確認の画面のまま(下書きは作らない)
    }

    [Fact]
    public async Task Keep_apart_is_blocked_while_programs_page_has_unsaved_changes_W138()
    {
        Ingested(Tbs, "安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)");
        var vm = await Create();
        await vm.Programs.LoadAsync();
        vm.Programs.SelectedProgram = vm.Programs.Programs[0];
        vm.Programs.DeleteProgramCommand.Execute(null);
        Assert.True(vm.Programs.HasChanges);

        vm.Tick();

        var row = Assert.Single(vm.Home.SiblingRows);
        Assert.False(row.KeepApartCommand.CanExecute(null));
        Assert.Equal("番組ページに保存していない変更があるため、分けておけません", vm.Home.KeepApartBlocked); // W-138

        var before = File.ReadAllText(ProgramsPath);
        await row.KeepApartCommand.ExecuteAsync(null); // 押せても(キーボード等)書かない
        Assert.Equal(before, File.ReadAllText(ProgramsPath));
        Assert.Equal(ProgramsPageViewModel.KeepApartBlocked, vm.Home.AttentionNote);
    }

    [Fact]
    public async Task Keep_apart_with_comments_goes_to_a_draft_instead_of_dropping_them()
    {
        Ingested(Tbs, "安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)");
        var withComment = Sample.Replace("\"Programs\": [", "// 手で書いたメモ\n  \"Programs\": [");
        var vm = await Create(withComment);

        await vm.Home.SiblingRows[0].KeepApartCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Programs.IsKeepFormMode);

        Assert.Equal(withComment, File.ReadAllText(ProgramsPath)); // その場では書かない(保存の前に W-260 で確かめる)
        Assert.Equal(ManagementPage.Programs, vm.CurrentPage);
        Assert.Equal("分けておく兄弟フォルダを追加", vm.Programs.FormHeading);
        Assert.Equal("安住紳一郎の日曜天国(1)\n安住紳一郎の日曜天国(2)", vm.Programs.FormFolders);
        Assert.Equal(ProgramsPageViewModel.KeepDraftFromAttention, vm.Programs.FormNotice);
        Assert.Null(vm.Home.AttentionNote);
    }

    [Fact]
    public async Task Keep_apart_that_finds_the_file_changed_shows_reason_W139()
    {
        Ingested(Tbs, "安住紳一郎の日曜天国(1)", "安住紳一郎の日曜天国(2)");
        var vm = await Create();
        File.WriteAllText(ProgramsPath, "{ \"Programs\": [ ");

        await vm.Home.SiblingRows[0].KeepApartCommand.ExecuteAsync(null);

        Assert.StartsWith("分けておく組を書けませんでした: programs.json を読めません(JSON が壊れています)", vm.Home.AttentionNote);
        Assert.Equal("{ \"Programs\": [ ", File.ReadAllText(ProgramsPath));
    }
}
