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

// 管理画面での差し替え放送の判断(#235・ADR-0021)。要対応の「この番組の回」「取り込まない」「別の番組…」(W-159)で番組ページの
// 下書きに積み(ホームに留まる)、番組ページの「ファイル名の番組名の判断」(W-230)で見る・直す・消す・保存する。
// 文言の ID は docs/mockups/management-window.md。programs.json は一時フォルダに実物を置き、要対応は本番と同じ計算で出す。
public sealed class SubstituteDecisionUiTests : IDisposable
{
    private const string Lf = "ニッポン放送";
    private const string Chari = "パンサー向井のチャリで30分";
    private const string Shinbo = "辛坊治郎 ズーム そこまで言うか！";
    private const string Baseball = "プロ野球 実況中継";

    private const string Sample = """
        {
          "Programs": [],
          "FilenamePrograms": [
            { "Match": { "Station": "TBSラジオ", "Folder": "パンサー向井のふらっと(1)", "FilenameProgram": "パンサー向井の#ふらっと (1)" },
              "Treat": "SameProgram" },
            { "Match": { "Station": "文化放送", "Folder": "日向坂46の「ひ」", "FilenameProgram": "プロ野球" },
              "Treat": "Skip", "Note": "実況中継" },
            { "Match": { "Station": "TOKYO FM", "Folder": "ベルクpresents日向坂46の余計な事までやりましょう", "FilenameProgram": "土屋圭市のくるまの話" },
              "Treat": "OtherProgram", "Album": "土屋圭市のくるまの話" }
          ]
        }
        """;

    private sealed class FakeControl : IServiceControl
    {
        public List<string> Calls { get; } = [];
        public ProgramMasterConfig? PlannedWith { get; private set; }
        public ServiceStatusSnapshot GetStatus() => Snapshot(completed: DateTimeOffset.UnixEpoch);
        public void RequestRescan() => Calls.Add("rescan");
        public Task<ScanRunResult> RunScanAsync() => throw new NotSupportedException();

        public ReloadResult RequestReload()
        {
            Calls.Add("reload");
            return ReloadResult.Ok();
        }

        public Task<ReapplyResult> PlanReapplyAsync() => throw new NotSupportedException();

        // 「確認」は揃っている予定を返す(「保存だけ」まで進めるため)。
        public Task<ReapplyResult> PlanReapplyAsync(ProgramMasterConfig programs)
        {
            PlannedWith = programs;
            return Task.FromResult(ReapplyResult.Ok(new ReapplyReport { Items = [], DryRun = true, UnintegratedSiblings = [] }));
        }

        public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
        public Task<ReapplyResult> PlanReapplyForCliAsync() => throw new NotSupportedException();
        public Task<ReapplyResult> ApplyReapplyPlanAsync(string planId) => throw new NotSupportedException();
        public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
    }

    private sealed class NoPathActions : IHistoryPathActions
    {
        public Task<string> RevealAsync(string path) => Task.FromResult("");
        public bool TryCopy(string path) => true;
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("radirec-substitute-ui-").FullName;
    private readonly List<string> _log = [];
    private readonly List<LedgerEntry> _ledger = [];
    private readonly FakeControl _control = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ProgramsPath => Path.Combine(_dir, "programs.json");

    private void Write(string content) => File.WriteAllText(ProgramsPath, content, new UTF8Encoding(false));

    // 番組フォルダ folder に、ファイル名の番組名 filenameProgram で取り込まれた回(フォルダ名を含まないので、判断が無ければ疑い)。
    private void Ingested(string station, string folder, string filenameProgram, int day)
    {
        _ledger.Add(new LedgerEntry
        {
            IdentityKey = $"{station}|{folder}|{day}",
            Station = station,
            Program = folder,
            BroadcastDate = new DateOnly(2026, 9, day),
            SourcePath = $"/radio/{station}/{folder}/[{station}]{filenameProgram}(TimeFree)_202609{day:00}230000.m4a",
            OutputPath = $"/out/{station}/{folder}/{folder} 2026-09-{day:00}.m4a",
            RuleVersion = "1",
            UsedFallback = true,
            ImportedAtUtc = DateTimeOffset.UnixEpoch,
        });
    }

    // 要対応は、ディスクの programs.json と台帳の代わりの記録から、本番の AttentionLoader と同じ計算で出す。
    private AttentionItems Compute()
    {
        var programs = ProgramsFileWriter.Read(ProgramsPath).Config;
        var resolver = new LedgerEpisodeResolver(
            new MockFileSystem().Path, new RajirekoImporter(),
            new StationResolver(new StationMasterConfig()), new ProgramResolver(programs));
        return AttentionCalculator.Compute(_ledger, resolver, new AttentionPrograms(programs));
    }

    private async Task<ProgramsPageViewModel> Page(string content = Sample)
    {
        Write(content);
        var vm = new ProgramsPageViewModel(_control, () => ProgramsPath, new ProgramsApplyGate(), _log.Add,
            now: () => new DateTime(2026, 9, 28, 12, 0, 0));
        await vm.LoadAsync();
        return vm;
    }

    private async Task<ManagementViewModel> Window(string content = """{ "Programs": [] }""")
    {
        Write(content);
        var vm = new ManagementViewModel(
            _control, null, () => [], new NoPathActions(), _log.Add,
            locateProgramsFile: () => ProgramsPath, now: () => new DateTime(2026, 9, 28, 12, 0, 0),
            computeAttention: Compute);
        await vm.Home.AttentionRefreshed;
        return vm;
    }

    private static async Task SaveOnly(ProgramsPageViewModel vm)
    {
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirmMode);
        await vm.SaveOnlyCommand.ExecuteAsync(null);
        Assert.True(vm.IsResultMode);
    }

    // ---------- 番組ページの一覧(W-230 / W-231)----------

    [Fact]
    public async Task Lists_decisions_with_treatment_and_album_W230_W231()
    {
        var vm = await Page();

        Assert.Equal(3, vm.FilenamePrograms.Count);
        Assert.Equal("3 件", vm.DecisionCountText); // W-210
        Assert.Equal(["この番組の回", "取り込まない", "別の番組"], vm.FilenamePrograms.Select(r => r.Treat)); // W-231
        Assert.Equal(["", "", "土屋圭市のくるまの話"], vm.FilenamePrograms.Select(r => r.Album));
        Assert.Equal("実況中継", vm.FilenamePrograms[1].Note);
        Assert.All(vm.FilenamePrograms, r => Assert.False(r.IsChanged));
        Assert.False(vm.HasChanges);
    }

    // ---------- 要対応から積む(W-159 / W-160 / W-161)----------

    [Fact]
    public async Task Staging_adds_an_unsaved_row_and_restaging_replaces_it()
    {
        var vm = await Page("""{ "Programs": [] }""");

        Assert.Null(vm.StageDecision(Lf, Chari, Shinbo, FilenameProgramTreatment.Skip));
        var row = Assert.Single(vm.FilenamePrograms);
        Assert.True(row.IsChanged);
        Assert.Null(row.Current.Album); // 「別の番組」でなければアルバム名を書かない
        Assert.Equal(1, vm.StagedDecisionCount);
        Assert.Equal(1, vm.ChangeCount);
        Assert.True(vm.IsListMode); // ページの画面は変えない
        Assert.Equal("取り込まない", vm.StagedDecisionLabel(Lf, Chari, Shinbo)); // W-160

        // 同じ組をもう一度押すと、行を足さずに置き換える。「別の番組」のアルバム名はファイル名の番組名が初期値。
        Assert.Null(vm.StageDecision(Lf, Chari, Shinbo, FilenameProgramTreatment.OtherProgram));
        row = Assert.Single(vm.FilenamePrograms);
        Assert.Equal(FilenameProgramTreatment.OtherProgram, row.Current.Treat);
        Assert.Equal(Shinbo, row.Current.Album);
        Assert.Equal(1, vm.StagedDecisionCount);
        Assert.Equal($"別の番組「{Shinbo}」", vm.StagedDecisionLabel(Lf, Chari, Shinbo));
    }

    [Fact]
    public async Task Staging_matches_the_group_by_match_key_like_ingest()
    {
        var vm = await Page();

        // 保存済みの「取り込まない」の組を、空白・全角の違う表記で積む → 同じ行を置き換え、元の表記を残す。
        Assert.Null(vm.StageDecision("文化放送", "日向坂46の「ひ」", "プロ野球", FilenameProgramTreatment.OtherProgram));
        Assert.Null(vm.StageDecision("文化放送", "日向坂４６の「ひ」", "プロ 野球", FilenameProgramTreatment.SameProgram));

        Assert.Equal(3, vm.FilenamePrograms.Count);
        var row = vm.FilenamePrograms[1];
        Assert.Equal("日向坂46の「ひ」", row.Folder);
        Assert.Equal(FilenameProgramTreatment.SameProgram, row.Current.Treat);
        Assert.Equal("実況中継", row.Note); // メモは残す
        Assert.Equal("この番組の回", vm.StagedDecisionLabel("文化放送", "日向坂46の「ひ」", "プロ野球"));
    }

    [Fact]
    public async Task Restaging_the_saved_decision_is_not_a_change()
    {
        var vm = await Page();

        Assert.Null(vm.StageDecision("文化放送", "日向坂46の「ひ」", "プロ野球", FilenameProgramTreatment.Skip));

        Assert.False(vm.HasChanges);
        Assert.Null(vm.StagedDecisionLabel("文化放送", "日向坂46の「ひ」", "プロ野球")); // 保存済みは W-160 を出さない
    }

    [Fact]
    public async Task Staging_is_blocked_while_editing_or_when_the_list_is_not_shown_W163_W164()
    {
        var vm = await Page();
        vm.AddProgramCommand.Execute(null); // 編集フォームの途中

        Assert.Equal(ProgramsPageViewModel.StageBlocked, vm.StageDecision(Lf, Chari, Shinbo, FilenameProgramTreatment.Skip)); // W-163
        Assert.Equal(3, vm.FilenamePrograms.Count);

        Write("{ 壊れた JSON");
        var broken = new ProgramsPageViewModel(_control, () => ProgramsPath, new ProgramsApplyGate(), _log.Add);
        await broken.LoadAsync();
        Assert.Equal(ProgramsPageViewModel.StageUnavailable, broken.StageDecision(Lf, Chari, Shinbo, FilenameProgramTreatment.Skip)); // W-164
    }

    // ---------- 保存 ----------

    [Fact]
    public async Task Saving_writes_staged_decisions_to_programs_json_and_keeps_the_others()
    {
        var vm = await Page();
        vm.StageDecision(Lf, Chari, Shinbo, FilenameProgramTreatment.OtherProgram);
        vm.StageDecision(Lf, Chari, Baseball, FilenameProgramTreatment.Skip);

        await vm.ConfirmCommand.ExecuteAsync(null);
        // 確認の dry-run は、積んだ判断を含む下書きで行い、変更前 → 変更後(W-241)に判断が出る。
        Assert.Equal(5, _control.PlannedWith!.FilenamePrograms.Count);
        Assert.Contains(Shinbo, vm.DiffAfter);
        Assert.Contains(Baseball, vm.DiffAfter);
        Assert.Equal(ProgramsDraft.Nothing, vm.DiffBefore);
        await vm.SaveOnlyCommand.ExecuteAsync(null);

        var saved = ProgramsFileWriter.Read(ProgramsPath).Config.FilenamePrograms;
        Assert.Equal(5, saved.Count);
        Assert.Equal(FilenameProgramTreatment.OtherProgram, saved[3].Treat);
        Assert.Equal(Shinbo, saved[3].Album);
        Assert.Equal(FilenameProgramTreatment.Skip, saved[4].Treat);
        Assert.Null(saved[4].Album);
        var json = File.ReadAllText(ProgramsPath);
        Assert.Contains("\"Treat\": \"Skip\"", json); // 名前で書く
        Assert.Contains("reload", _control.Calls); // 「保存だけ」も設定リロードする(保留した回を次の走査で取り込ませる)

        // 読み直した一覧では保存済み(積んだ判断は無くなる)。
        Assert.Equal(0, vm.StagedDecisionCount);
        Assert.Null(vm.StagedDecisionLabel(Lf, Chari, Shinbo));
    }

    [Fact]
    public async Task Draft_with_staged_decisions_has_the_same_values_as_the_saved_file()
    {
        // 「保存して適用」の worker は、保存して読み直した設定が確認のときの下書きと値で同じかを確かめ、違えば実行しない(W-278)。
        // 積んだ判断(「別の番組」のアルバム名の初期値・メモ無し等)が、書いて読み直しても同じ値になることを確かめる。
        var vm = await Page();
        vm.StageDecision(Lf, Chari, Shinbo, FilenameProgramTreatment.OtherProgram);
        vm.StageDecision(Lf, Chari, Baseball, FilenameProgramTreatment.Skip);
        vm.StageDecision("文化放送", "日向坂46の「ひ」", "プロ野球", FilenameProgramTreatment.SameProgram); // 保存済みの行を置き換える

        await SaveOnly(vm);

        var draft = new ReapplySettings(new StationMasterConfig(), _control.PlannedWith!);
        var saved = new ReapplySettings(new StationMasterConfig(), ProgramsFileWriter.Read(ProgramsPath).Config);
        Assert.True(draft.HasSameValuesAs(saved));
    }

    [Fact]
    public async Task Staging_other_program_over_an_empty_album_falls_back_to_the_filename_program()
    {
        // アルバム名が空の「別の番組」は取り込みが受け付けないので、積むときに空を引き継がない。
        var vm = await Page("""
            { "FilenamePrograms": [
              { "Match": { "Station": "ニッポン放送", "Folder": "パンサー向井のチャリで30分", "FilenameProgram": "辛坊治郎 ズーム そこまで言うか！" },
                "Treat": "Skip", "Album": " " } ] }
            """);

        vm.StageDecision(Lf, Chari, Shinbo, FilenameProgramTreatment.OtherProgram);

        Assert.Equal(Shinbo, Assert.Single(vm.FilenamePrograms).Current.Album);
    }

    [Fact]
    public async Task Deleting_and_reverting_decisions()
    {
        var vm = await Page();

        vm.SelectedDecision = vm.FilenamePrograms[2];
        vm.DeleteDecisionCommand.Execute(null);
        Assert.Equal(2, vm.FilenamePrograms.Count);
        Assert.Equal(1, vm.StagedDecisionCount);

        vm.RevertCommand.Execute(null); // W-209
        Assert.Equal(3, vm.FilenamePrograms.Count);
        Assert.False(vm.HasChanges);

        vm.SelectedDecision = vm.FilenamePrograms[2];
        vm.DeleteDecisionCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Contains("土屋圭市", vm.DiffBefore); // 消した判断は「変更前」に出る
        await vm.SaveOnlyCommand.ExecuteAsync(null);

        Assert.Equal(2, ProgramsFileWriter.Read(ProgramsPath).Config.FilenamePrograms.Count);
    }

    // ---------- 編集フォーム(W-232〜W-234)----------

    [Fact]
    public async Task Decision_form_validates_and_adds_W232_W234()
    {
        var vm = await Page();
        vm.AddDecisionCommand.Execute(null);
        Assert.True(vm.IsDecisionFormMode);
        Assert.True(vm.IsFormMode);
        Assert.False(vm.IsFoldersFormMode); // 複数行のフォルダ欄は出さない
        Assert.Equal("ファイル名の番組名の判断を追加", vm.FormHeading); // W-232

        vm.FormTreat = FilenameProgramTreatment.OtherProgram;
        Assert.True(vm.IsFormOtherProgram);
        vm.CommitFormCommand.Execute(null);
        Assert.Equal(
            [ProgramsPageViewModel.StationRequired, ProgramsPageViewModel.FolderRequired,
             ProgramsPageViewModel.FilenameProgramRequired, ProgramsPageViewModel.OtherAlbumRequired],
            vm.FormErrors); // W-224 / W-234
        Assert.True(vm.IsDecisionFormMode);

        // 保存済みの組と照合キーで同じ → 重複。
        vm.FormStation = "文化放送";
        vm.FormFolder = "日向坂46の「ひ」";
        vm.FormFilenameProgram = " プロ野球 ";
        vm.FormAlbum = "プロ野球中継";
        vm.CommitFormCommand.Execute(null);
        Assert.Equal([ProgramsPageViewModel.DuplicateDecision], vm.FormErrors);

        vm.FormFilenameProgram = "日本シリーズ";
        vm.FormNote = "  ";
        vm.CommitFormCommand.Execute(null);

        Assert.True(vm.IsListMode);
        var added = vm.FilenamePrograms[^1];
        Assert.Same(added, vm.SelectedDecision);
        Assert.Null(added.Original);
        Assert.Equal("日本シリーズ", added.FilenameProgram);
        Assert.Equal("プロ野球中継", added.Current.Album);
        Assert.Null(added.Current.Note);
    }

    [Fact]
    public async Task Editing_a_decision_away_from_other_program_drops_the_album()
    {
        var vm = await Page();
        vm.SelectedDecision = vm.FilenamePrograms[2]; // 別の番組「土屋圭市のくるまの話」
        vm.EditDecisionCommand.Execute(null);
        Assert.Equal("ファイル名の番組名の判断を編集", vm.FormHeading);
        Assert.Equal("土屋圭市のくるまの話", vm.FormAlbum);

        vm.FormTreat = FilenameProgramTreatment.Skip;
        vm.CommitFormCommand.Execute(null); // 自分の行とは重複にしない

        var row = vm.FilenamePrograms[2];
        Assert.True(row.IsChanged);
        Assert.Equal(FilenameProgramTreatment.Skip, row.Current.Treat);
        Assert.Null(row.Current.Album);
        Assert.Equal("", row.Album);
    }

    [Fact]
    public async Task Editing_a_decision_to_other_program_starts_from_the_filename_program()
    {
        var vm = await Page();
        vm.SelectedDecision = vm.FilenamePrograms[1]; // 取り込まない「プロ野球」
        vm.EditDecisionCommand.Execute(null);

        Assert.Equal("プロ野球", vm.FormAlbum);
    }

    // ---------- 照合の種類(W-235 / W-236。#240)----------

    private const string WithModes = """
        { "FilenamePrograms": [
            { "Match": { "Station": "TOKYO FM", "Folder": "ベルク", "FilenameProgram": "土屋圭市のくるまの話" }, "Treat": "Skip" },
            { "Match": { "Station": "TOKYO FM", "Folder": "ベルク", "FilenameProgram": "土屋圭市のくるまの話", "Mode": "Prefix" },
              "Treat": "OtherProgram", "Album": "土屋圭市のくるまの話" },
            { "Match": { "Station": "TBSラジオ", "Folder": "ねごと", "FilenameProgram": "川島明のねごと", "Mode": "Contains" },
              "Treat": "SameProgram" } ] }
        """;

    [Fact]
    public async Task Lists_the_match_mode_of_each_decision_W235()
    {
        var vm = await Page(WithModes);

        // 完全一致と前方一致に同じ文字列を書いても、別の判断として並ぶ。
        Assert.Equal(["完全一致", "前方一致", "部分一致"], vm.FilenamePrograms.Select(r => r.MatchMode));
        Assert.All(vm.FilenamePrograms, r => Assert.False(r.IsChanged));
    }

    [Fact]
    public async Task Decision_form_edits_the_match_mode_and_saves_it_by_name()
    {
        // 要対応から積んだ完全一致の行を開き、照合を前方一致に変えて文字列を縮める(#240 の直し方)。
        var vm = await Page("""{ "Programs": [] }""");
        vm.StageDecision(Lf, Chari, Shinbo, FilenameProgramTreatment.Skip);
        vm.SelectedDecision = vm.FilenamePrograms[0];
        vm.EditDecisionCommand.Execute(null);
        Assert.Equal(FilenameProgramMatchMode.Exact, vm.FormMatchMode);

        vm.FormMatchMode = FilenameProgramMatchMode.Prefix;
        vm.FormFilenameProgram = "辛坊治郎";
        vm.CommitFormCommand.Execute(null);

        var row = Assert.Single(vm.FilenamePrograms);
        Assert.Equal(FilenameProgramMatchMode.Prefix, row.Current.Match.Mode);
        Assert.Equal("前方一致", row.MatchMode);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Contains("\"Mode\": \"Prefix\"", vm.DiffAfter); // W-241 も書き出す形で見せる
        await vm.SaveOnlyCommand.ExecuteAsync(null);

        var saved = Assert.Single(ProgramsFileWriter.Read(ProgramsPath).Config.FilenamePrograms);
        Assert.Equal(FilenameProgramMatchMode.Prefix, saved.Match.Mode);
        Assert.Equal("辛坊治郎", saved.Match.FilenameProgram);
    }

    [Fact]
    public async Task Decision_form_treats_the_same_string_with_another_mode_as_another_decision()
    {
        var vm = await Page(WithModes);
        vm.AddDecisionCommand.Execute(null);
        vm.FormStation = "TOKYO FM";
        vm.FormFolder = "ベルク";
        vm.FormFilenameProgram = "土屋圭市のくるまの話";
        vm.FormTreat = FilenameProgramTreatment.SameProgram;

        vm.FormMatchMode = FilenameProgramMatchMode.Prefix;
        vm.CommitFormCommand.Execute(null);
        Assert.Equal([ProgramsPageViewModel.DuplicateDecision], vm.FormErrors); // 同じ種類・同じ照合キー

        vm.FormMatchMode = FilenameProgramMatchMode.Contains;
        vm.CommitFormCommand.Execute(null);
        Assert.True(vm.IsListMode);
        Assert.Equal(4, vm.FilenamePrograms.Count);
    }

    [Fact]
    public async Task Decision_form_rejects_an_empty_match_key_for_prefix_and_contains()
    {
        // 照合キーが空の前方一致・部分一致は、取り込みの起動と設定リロードが受け付けない。
        var vm = await Page();
        vm.AddDecisionCommand.Execute(null);
        vm.FormStation = Lf;
        vm.FormFolder = Chari;
        vm.FormFilenameProgram = "　 ";
        vm.FormMatchMode = FilenameProgramMatchMode.Contains;

        vm.CommitFormCommand.Execute(null);

        Assert.Equal([ProgramsPageViewModel.FilenameProgramRequired], vm.FormErrors);
    }

    [Fact]
    public async Task Staged_label_follows_a_row_narrowed_to_prefix_W160()
    {
        // 積んだ完全一致の行を番組ページで前方一致に縮めても、ホームのその組に W-160 が出続ける(出ないと、もう一度押して
        // 別の完全一致の行を足し、保存後は完全一致が優先されて前方一致が効かなくなる)。
        const string guest1 = "野村萬斎のラジオで福袋【ゲスト】中越典子";
        const string guest2 = "野村萬斎のラジオで福袋【ゲスト】羽生結弦";
        var vm = await Page("""{ "Programs": [] }""");
        vm.StageDecision(Lf, Chari, guest1, FilenameProgramTreatment.OtherProgram);
        vm.SelectedDecision = vm.FilenamePrograms[0];
        vm.EditDecisionCommand.Execute(null);
        vm.FormMatchMode = FilenameProgramMatchMode.Prefix;
        vm.FormFilenameProgram = "野村萬斎のラジオで福袋";
        vm.FormAlbum = "野村萬斎のラジオで福袋";
        vm.CommitFormCommand.Execute(null);

        Assert.Equal("別の番組「野村萬斎のラジオで福袋」", vm.StagedDecisionLabel(Lf, Chari, guest1));
        Assert.Equal("別の番組「野村萬斎のラジオで福袋」", vm.StagedDecisionLabel(Lf, Chari, guest2)); // 同じ判断が当たる別の組にも
        Assert.Null(vm.StagedDecisionLabel(Lf, Chari, Shinbo)); // 当たらない組には出さない
        Assert.Null(vm.StagedDecisionLabel("TBSラジオ", Chari, guest1)); // 局が違えば当てない

        // 当たる行が複数あれば、取り込みと同じ優先(完全一致が前方一致に勝つ)。
        vm.StageDecision(Lf, Chari, guest2, FilenameProgramTreatment.Skip);
        Assert.Equal("取り込まない", vm.StagedDecisionLabel(Lf, Chari, guest2));
        Assert.Equal("別の番組「野村萬斎のラジオで福袋」", vm.StagedDecisionLabel(Lf, Chari, guest1));
    }

    [Fact]
    public async Task Diff_does_not_show_the_exact_mode_W241()
    {
        // 確認の「変更後」(W-241)は programs.json の書き出しと同じ形。完全一致の判断に "Mode" を出さない。
        var vm = await Page("""{ "Programs": [] }""");
        vm.StageDecision(Lf, Chari, Shinbo, FilenameProgramTreatment.Skip);
        vm.StageDecision(Lf, Chari, Baseball, FilenameProgramTreatment.Skip);
        vm.SelectedDecision = vm.FilenamePrograms[1];
        vm.EditDecisionCommand.Execute(null);
        vm.FormMatchMode = FilenameProgramMatchMode.Contains;
        vm.FormFilenameProgram = "プロ野球";
        vm.CommitFormCommand.Execute(null);

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains(Shinbo, vm.DiffAfter);
        Assert.DoesNotContain("Exact", vm.DiffAfter);
        Assert.Single(vm.DiffAfter.Split("\"Mode\"")[1..]); // 部分一致の行にだけ出る
        Assert.Contains("\"Mode\": \"Contains\"", vm.DiffAfter);
    }

    [Fact]
    public async Task Staging_stays_exact_even_when_a_prefix_decision_has_the_same_string()
    {
        // 要対応から積むのは常に完全一致。同じ文字列の前方一致の行は置き換えない。
        var vm = await Page(WithModes);

        vm.StageDecision("TOKYO FM", "ベルク", "土屋圭市のくるまの話", FilenameProgramTreatment.SameProgram);

        Assert.Equal(FilenameProgramTreatment.SameProgram, vm.FilenamePrograms[0].Current.Treat);
        Assert.Equal(FilenameProgramTreatment.OtherProgram, vm.FilenamePrograms[1].Current.Treat);
        Assert.Equal(3, vm.FilenamePrograms.Count);
    }

    // ---------- ホーム(W-159〜W-164)----------

    [Fact]
    public async Task Home_buttons_stage_into_the_programs_page_and_stay_on_home_W159_W161()
    {
        Ingested(Lf, Chari, Shinbo, 21);
        Ingested(Lf, Chari, Baseball, 22);
        var vm = await Window();
        var home = vm.Home;
        Assert.Equal(2, home.SubstituteRows.Count);
        Assert.All(home.SubstituteRows, r => Assert.False(r.IsStaged));
        Assert.Null(home.StagedSummary);

        var baseball = home.SubstituteRows.Single(r => r.Suspect.FilenameProgram == Baseball);
        await baseball.SkipCommand.ExecuteAsync(null);

        Assert.Equal(ManagementPage.Home, vm.CurrentPage); // ページは移らない
        Assert.Equal("下書き: 取り込まない", baseball.Staged); // W-160
        Assert.Equal(
            "番組ページに保存していない判断が 1 件あります。番組ページで「確認」→「保存して適用」を押すと反映します",
            home.StagedSummary); // W-161
        Assert.Null(home.SubstituteNote);
        Assert.Equal(1, vm.Programs.StagedDecisionCount);
        Assert.DoesNotContain("FilenamePrograms", File.ReadAllText(ProgramsPath)); // ファイルには書かない

        var shinbo = home.SubstituteRows.Single(r => r.Suspect.FilenameProgram == Shinbo);
        await shinbo.OtherProgramCommand.ExecuteAsync(null);
        Assert.Equal($"下書き: 別の番組「{Shinbo}」", shinbo.Staged);
        await shinbo.SameProgramCommand.ExecuteAsync(null); // 押し直すと置き換える
        Assert.Equal("下書き: この番組の回", shinbo.Staged);
        Assert.Equal("番組ページに保存していない判断が 2 件あります。番組ページで「確認」→「保存して適用」を押すと反映します",
            home.StagedSummary);

        home.ShowProgramsCommand.Execute(null); // W-162
        Assert.Equal(ManagementPage.Programs, vm.CurrentPage);
    }

    [Fact]
    public async Task Home_follows_edits_on_the_programs_page()
    {
        Ingested(Lf, Chari, Shinbo, 21);
        var vm = await Window();
        var row = Assert.Single(vm.Home.SubstituteRows);
        await row.SkipCommand.ExecuteAsync(null);
        Assert.True(row.IsStaged);

        // 番組ページで「変更を取り消す」→ タイマーの Refresh で積んだ印と件数が消える。
        vm.Programs.RevertCommand.Execute(null);
        vm.Tick();

        Assert.False(row.IsStaged);
        Assert.Null(vm.Home.StagedSummary);
    }

    [Fact]
    public async Task Staging_blocks_keep_apart_and_saved_reapply_like_other_unsaved_work()
    {
        Ingested(Lf, Chari, Shinbo, 21);
        var vm = await Window();
        await Assert.Single(vm.Home.SubstituteRows).SkipCommand.ExecuteAsync(null);

        Assert.True(vm.Programs.HasUnsavedWork);
        Assert.Equal(ProgramsPageViewModel.SavedConfirmBlocked, vm.Programs.SavedConfirmBlockedReason); // W-256
    }

    [Fact]
    public async Task Home_shows_why_it_could_not_stage_W163()
    {
        Ingested(Lf, Chari, Shinbo, 21);
        var vm = await Window();
        await vm.Programs.OnShownAsync();
        vm.Programs.AddDecisionCommand.Execute(null); // 番組ページで編集の途中

        var row = Assert.Single(vm.Home.SubstituteRows);
        await row.SkipCommand.ExecuteAsync(null);

        Assert.Equal(ProgramsPageViewModel.StageBlocked, vm.Home.SubstituteNote);
        Assert.True(vm.Home.IsSubstituteNoteVisible);
        Assert.False(row.IsStaged);
        Assert.Empty(vm.Programs.FilenamePrograms);
    }

    [Fact]
    public async Task Staging_from_the_result_screen_returns_the_page_to_the_list_first()
    {
        Ingested(Lf, Chari, Shinbo, 21);
        Ingested(Lf, Chari, Baseball, 22);
        var vm = await Window();
        await vm.Home.SubstituteRows[0].SkipCommand.ExecuteAsync(null);
        await SaveOnly(vm.Programs); // 番組ページは結果の画面のまま

        await vm.Home.AttentionRefreshed;
        vm.Home.RefreshAttention();
        await vm.Home.AttentionRefreshed;
        var remaining = Assert.Single(vm.Home.SubstituteRows); // 保存した組は要対応から消える
        await remaining.SkipCommand.ExecuteAsync(null);

        Assert.True(vm.Programs.IsListMode);
        Assert.True(remaining.IsStaged);
        Assert.Null(vm.Home.SubstituteNote);
    }

    [Fact]
    public async Task Saving_the_staged_decision_clears_the_suspect_from_attention()
    {
        Ingested(Lf, Chari, Shinbo, 21);
        Ingested(Lf, Chari, Shinbo, 28);
        var vm = await Window();
        await Assert.Single(vm.Home.SubstituteRows).OtherProgramCommand.ExecuteAsync(null);

        vm.Home.ShowProgramsCommand.Execute(null); // 番組ページへ移って保存する
        await SaveOnly(vm.Programs);
        vm.CurrentPage = ManagementPage.Home; // ホームへ戻ると要対応を計算し直す
        await vm.Home.AttentionRefreshed;

        Assert.Empty(vm.Home.SubstituteRows);
        Assert.Null(vm.Home.StagedSummary);
        // 取り込み済みの 2 回は、判断のアルバムと食い違うので「再適用が要る番組」に移る(再適用で動かす)。
        var drift = Assert.Single(vm.Home.DriftRows);
        Assert.Equal(Shinbo, drift.Drift!.ToAlbum);
        Assert.Equal(2, drift.Drift.Count);
    }
}
