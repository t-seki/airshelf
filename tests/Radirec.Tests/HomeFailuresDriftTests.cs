using Radirec.Core.Attention;
using Radirec.Core.Ledger;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;
using Radirec.Gui.Core.Management;
using Radirec.Hosting.Control;
using static Radirec.Tests.ServiceStatusTextTests;

namespace Radirec.Tests;

// 管理画面のホームの要対応「解決していない取込失敗」「再適用が要る番組」(#156・ADR-0018)。
// 文言の ID は docs/mockups/management-window.md。要対応の計算そのもの(#172)は AttentionCalculatorTests が見るので、
// ここでは計算の結果を差し替えて、ホームの出し方と「再適用の確認へ」(W-136)の入口のつなぎ方を見る。
public sealed class HomeFailuresDriftTests
{
    private const string Tbs = "TBSラジオ";

    private sealed class FakeControl : IServiceControl
    {
        public ServiceStatusSnapshot Status { get; set; } = Snapshot(completed: DateTimeOffset.UnixEpoch);
        public void RequestRescan() { }
        public Task<ScanRunResult> RunScanAsync() => throw new NotSupportedException();
        public ServiceStatusSnapshot GetStatus() => Status;
        public ReloadResult RequestReload() => ReloadResult.Ok();
        public Task<ReapplyResult> PlanReapplyAsync() => throw new NotSupportedException();
        public Task<ReapplyResult> PlanReapplyAsync(Radirec.Core.Configuration.ProgramMasterConfig programs) => throw new NotSupportedException();
        public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
        public Task<ReapplyResult> PlanReapplyForCliAsync() => throw new NotSupportedException();
        public Task<ReapplyResult> ApplyReapplyPlanAsync(string planId) => throw new NotSupportedException();
        public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
    }

    private sealed class FakeAttention(Func<AttentionItems> compute) : IAttentionActions
    {
        public AttentionItems Compute() => compute();
        public void Integrate(SiblingFolderGroup group) { }
        public Task<string?> KeepApartAsync(SiblingFolderGroup group) => Task.FromResult<string?>(null);
        public string? KeepApartBlockedReason => null;
    }

    private sealed class FakeEntry : ISavedReapplyEntry
    {
        public string? BlockedReason { get; set; }
        public SavedReapplyStart Start { get; set; } = SavedReapplyStart.Confirm;
        public TaskCompletionSource? Gate { get; set; }
        public int Starts { get; private set; }

        public async Task<SavedReapplyStart> StartAsync()
        {
            Starts++;
            if (Gate is { } gate)
                await gate.Task;
            return Start;
        }
    }

    private sealed class FakePaths : IHistoryPathActions
    {
        public List<string> Revealed { get; } = [];
        public Task<string> RevealAsync(string path)
        {
            Revealed.Add(path);
            return Task.FromResult($"エクスプローラーで開きました: {path}");
        }
        public bool TryCopy(string path) => true;
    }

    private readonly FakeControl _control = new();
    private readonly FakeEntry _entry = new();
    private readonly FakePaths _paths = new();
    private readonly List<string> _log = [];
    private AttentionItems _items = new();

    private async Task<HomeViewModel> Create(bool withEntry = true)
    {
        var home = new HomeViewModel(_control, null, () => [], _log.Add, showAll: () => { },
            zone: TimeZoneInfo.Utc, attention: new FakeAttention(() => _items), pathActions: _paths,
            savedReapply: withEntry ? _entry : null);
        await home.AttentionRefreshed;
        return home;
    }

    private static IngestFailureRecord Failure(string path, string? reason = "壊れています", DateTimeOffset? at = null)
        => new(path, reason, at);

    private static SettingsDriftGroup Drift(string from, string to, int count = 2) => new(Tbs, from, to, count);

    private static UnintegratedSiblingGroup Sibling()
        => new(new SiblingFolderGroup(Tbs, "番組", ["番組(1)", "番組(2)"]), ["番組(2)"]);

    // ---------- 一覧と件数 ----------

    [Fact]
    public async Task Failures_and_drift_are_listed_and_counted_with_siblings_W120()
    {
        _items = new AttentionItems
        {
            UnintegratedSiblings = [Sibling()],
            UnresolvedFailures = [Failure(@"I:\radio\TBS\番組\a.m4a")],
            SettingsDrift = [Drift("前", "後")],
        };

        var home = await Create();

        Assert.True(home.HasSiblings);
        Assert.True(home.HasFailures);
        Assert.True(home.HasDrift);
        Assert.True(home.HasAnyAttention);
        Assert.Equal(3, home.AttentionCount);
        Assert.Equal("要対応 3 件", home.AttentionCountText); // W-120
        Assert.False(home.IsAttentionEmpty);
    }

    [Fact]
    public async Task None_of_the_three_shows_W121()
    {
        var home = await Create();

        Assert.False(home.HasAnyAttention);
        Assert.True(home.IsAttentionEmpty); // W-121
        Assert.Null(home.AttentionCountText);
    }

    [Fact]
    public async Task Only_failures_do_not_show_W121()
    {
        _items = new AttentionItems { UnresolvedFailures = [Failure(@"I:\radio\a.m4a")] };

        var home = await Create();

        Assert.False(home.IsAttentionEmpty);
        Assert.Equal("要対応 1 件", home.AttentionCountText);
    }

    // ---------- 解決していない取込失敗(W-128〜W-132) ----------

    [Fact]
    public async Task Failure_row_shows_file_name_reason_and_date_W129()
    {
        _items = new AttentionItems
        {
            UnresolvedFailures = [Failure(@"I:\radio\TBSラジオ\番組\[TBS]番組_20240923.m4a", "タグを読めません",
                new DateTimeOffset(2026, 9, 25, 3, 4, 0, TimeSpan.Zero))],
        };

        var row = Assert.Single((await Create()).FailureRows);

        Assert.Equal("[TBS]番組_20240923.m4a", row.FileName);
        Assert.Equal("タグを読めません", row.Reason);
        Assert.Equal("失敗: 2026-09-25 03:04", row.FailedAt);
        Assert.True(row.HasFailedAt);
    }

    [Fact]
    public async Task Failure_recorded_before_the_migration_has_no_reason_and_no_date_W130()
    {
        _items = new AttentionItems { UnresolvedFailures = [Failure("/radio/a.m4a", reason: null, at: null)] };

        var row = Assert.Single((await Create()).FailureRows);

        Assert.Equal("a.m4a", row.FileName);
        Assert.Equal("(理由の記録なし)", row.Reason); // W-130
        Assert.Null(row.FailedAt); // 日時の行は出さない
        Assert.False(row.HasFailedAt);
    }

    [Fact]
    public async Task Reveal_source_opens_the_source_path_and_shows_the_result_W132()
    {
        const string path = @"I:\radio\TBSラジオ\番組\a.m4a";
        _items = new AttentionItems { UnresolvedFailures = [Failure(path)] };
        var home = await Create();
        var row = Assert.Single(home.FailureRows);

        Assert.True(row.RevealCommand.CanExecute(null)); // いつでも押せる(取込元から消えた失敗は走査が記録を消す。#172)
        await row.RevealCommand.ExecuteAsync(null);

        Assert.Equal([path], _paths.Revealed);
        Assert.Equal($"エクスプローラーで開きました: {path}", home.FailureNote); // W-146
        Assert.True(home.IsFailureNoteVisible);
    }

    [Fact]
    public async Task Resolved_failure_disappears_when_attention_is_computed_again()
    {
        _items = new AttentionItems { UnresolvedFailures = [Failure("/radio/a.m4a")] };
        var home = await Create();
        Assert.True(home.HasFailures);

        _items = new AttentionItems(); // 取り込みに成功した(ストアから消えた)
        await home.RefreshAttentionAsync();

        Assert.False(home.HasFailures);
        Assert.True(home.IsAttentionEmpty);
    }

    // ---------- 再適用が要る番組(W-134〜W-136) ----------

    [Fact]
    public void Drift_lines_W135()
    {
        Assert.Equal("TBSラジオ「前」→「後」 2 回", AttentionText.DriftLine(Drift("前", "後")));
        Assert.Equal("TBSラジオ「番組」 パートの番号が変わる 3 回", AttentionText.DriftLine(Drift("番組", "番組", 3)));
    }

    [Fact]
    public async Task Reapply_confirm_starts_the_shared_entry_W136()
    {
        _items = new AttentionItems { SettingsDrift = [Drift("前", "後")] };
        var home = await Create();

        Assert.True(home.CanReapply);
        Assert.Null(home.ReapplyNote);
        await home.ReapplyCommand.ExecuteAsync(null);

        Assert.Equal(1, _entry.Starts);
        Assert.Null(home.ReapplyNote); // 確認の画面へ移った(移すのは入口)
    }

    [Fact]
    public async Task Aligned_or_failed_result_stays_on_home_as_one_line_W254_W255()
    {
        _items = new AttentionItems { SettingsDrift = [Drift("前", "後")] };
        var home = await Create();

        _entry.Start = SavedReapplyStart.Stay("取り込み済みの回は今の設定と揃っています", isWarning: false);
        await home.ReapplyCommand.ExecuteAsync(null);
        Assert.Equal("取り込み済みの回は今の設定と揃っています", home.ReapplyNote); // W-254
        Assert.False(home.IsReapplyNoteWarning);

        _entry.Start = SavedReapplyStart.Stay(ProgramsPageViewModel.SavedAborted("理由"), isWarning: true);
        await home.ReapplyCommand.ExecuteAsync(null);
        Assert.Equal(ProgramsPageViewModel.SavedAborted("理由"), home.ReapplyNote); // W-255
        Assert.True(home.IsReapplyNoteWarning);
    }

    [Fact]
    public async Task Previous_result_is_kept_while_drift_is_the_same_and_cleared_when_it_changes()
    {
        _items = new AttentionItems { SettingsDrift = [Drift("前", "後")] };
        var home = await Create();
        _entry.Start = SavedReapplyStart.Stay(ProgramsPageViewModel.SavedAborted("理由"), isWarning: true);
        await home.ReapplyCommand.ExecuteAsync(null);
        Assert.Equal(ProgramsPageViewModel.SavedAborted("理由"), home.ReapplyNote); // W-255

        // スキャンが終わって計算し直しても、食い違いの中身が同じなら前の結果を残す。
        _items = new AttentionItems { SettingsDrift = [Drift("前", "後")] };
        await home.RefreshAttentionAsync();
        Assert.Equal(ProgramsPageViewModel.SavedAborted("理由"), home.ReapplyNote);

        // 食い違いが解消し、別の食い違いが出た。押していないのに前の結果を出さない。
        _items = new AttentionItems { SettingsDrift = [Drift("別", "設定")] };
        await home.RefreshAttentionAsync();
        Assert.Null(home.ReapplyNote);
        Assert.False(home.IsReapplyNoteWarning);
    }

    [Fact]
    public async Task Unsaved_work_on_programs_page_blocks_W136_and_is_read_again_every_tick_W256()
    {
        _items = new AttentionItems { SettingsDrift = [Drift("前", "後")] };
        var home = await Create();
        Assert.True(home.CanReapply);

        _entry.BlockedReason = "番組ページに保存していない変更があります";
        home.Refresh(); // シェルのタイマー(2 秒ごと)

        Assert.False(home.CanReapply);
        Assert.False(home.ReapplyCommand.CanExecute(null));
        Assert.Equal("番組ページに保存していない変更があります", home.ReapplyNote);

        _entry.BlockedReason = null;
        home.Refresh();

        Assert.True(home.CanReapply);
        Assert.Null(home.ReapplyNote);
    }

    [Fact]
    public async Task Stopped_ingest_blocks_W136_W119()
    {
        _items = new AttentionItems { SettingsDrift = [Drift("前", "後")] };
        var home = await Create();

        _control.Status = Snapshot(running: false);
        home.Refresh();

        Assert.False(home.CanReapply);
        Assert.Equal(AttentionText.ReapplyUnavailable, home.ReapplyNote); // W-119
    }

    [Fact]
    public async Task While_checking_the_button_is_disabled_and_shows_W240()
    {
        _items = new AttentionItems { SettingsDrift = [Drift("前", "後")] };
        var home = await Create();
        _entry.Gate = new TaskCompletionSource();

        var running = home.ReapplyCommand.ExecuteAsync(null);

        Assert.False(home.CanReapply); // 2 回押せない
        Assert.Equal(ProgramsPageViewModel.Checking, home.ReapplyNote); // W-240
        _entry.Gate.SetResult();
        await running;
        Assert.True(home.CanReapply);
    }

    [Fact]
    public async Task No_drift_hides_the_note_and_cannot_press()
    {
        var home = await Create();

        Assert.False(home.CanReapply);
        Assert.Null(home.ReapplyNote);
    }

    [Fact]
    public async Task Without_the_entry_W136_cannot_be_pressed()
    {
        _items = new AttentionItems { SettingsDrift = [Drift("前", "後")] };
        var home = await Create(withEntry: false);

        Assert.False(home.CanReapply);
    }

    // 管理画面とのつなぎ(ホームから確認の画面へ移り、戻る)は SavedReapplyConfirmTests が見る。
}
