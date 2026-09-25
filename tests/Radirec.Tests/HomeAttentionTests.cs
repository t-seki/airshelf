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
        public ServiceStatusSnapshot GetStatus() => Status;
        public ReloadResult RequestReload() => ReloadResult.Ok();
        public Task<ReapplyResult> PlanReapplyAsync() => throw new NotSupportedException();
        // 番組ページの「確認」→「保存だけ」で結果の画面まで進めるための dry-run(揃っている予定)。
        public Task<ReapplyResult> PlanReapplyAsync(ProgramMasterConfig programs) => Task.FromResult(ReapplyResult.Ok(
            new ReapplyReport { Items = [], DryRun = true, UnintegratedSiblings = [] }));
        public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
        public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
    }

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
            locateProgramsFile: () => ProgramsPath, now: () => new DateTime(2026, 9, 25, 12, 0, 0),
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
        Ingested(Tbs, "パンサー向井のふらっと(1)", "パンサー向井のふらっと(2)"); // 統合済み

        var home = (await Create()).Home;

        Assert.Empty(home.SiblingRows);
        Assert.True(home.IsAttentionEmpty);
        Assert.Null(home.AttentionCountText);
        Assert.Equal("要対応はありません", AttentionText.None);
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
        vm.CurrentPage = ManagementPage.Programs;
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
        vm.CurrentPage = ManagementPage.Programs;
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
