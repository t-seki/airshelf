using Radirec.Core.Configuration;
using Radirec.Core.Reapply;
using Radirec.Hosting;
using Radirec.Hosting.Control;

namespace Radirec.Tests;

/// <summary>
/// 再適用のジョブの中身(<see cref="ReapplyJobs"/>)。常駐の直列ループが処理する、管理画面・トレイの確認と実行(#126 / #154)と、
/// CLI の予定 ID による確認と実行(#181・ADR-0020)。常駐(設定の差し替え・再適用の組み立て)はフェイクにする。
/// </summary>
public sealed class ReapplyJobsTests
{
    private static ReapplyItem Item(string from) => new()
    {
        Action = ReapplyAction.Move,
        Station = "TBSラジオ",
        FromAlbum = "安住紳一郎の日曜天国(1)",
        ToAlbum = "安住紳一郎の日曜天国",
        BroadcastDate = new DateOnly(2026, 9, 20),
        FromPath = from,
        ToPath = @"Z:\out\安住紳一郎の日曜天国\" + Path.GetFileName(from),
        SourcePath = @"I:\radio\" + Path.GetFileName(from),
    };

    private static Task<ReapplyResult> Run(ReapplyJobs jobs, ReapplyJobKind kind, ReapplyReport? plan = null, string? planId = null) =>
        jobs.RunAsync(new ReapplyJob(kind, Plan: plan, PlanId: planId), CancellationToken.None);

    // ---- CLI の確認 ----

    [Fact]
    public async Task CLIの確認は設定リロードせず_予定とIDを返す()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);

        var planned = await Run(jobs, ReapplyJobKind.PlanForCli);

        Assert.NotNull(planned.Report);
        Assert.True(planned.Report!.DryRun);
        Assert.Equal(ReapplyPlanId.Of(planned.Report), planned.PlanId);
        Assert.Equal(0, host.ConfigLoads);
        Assert.Same(host.InitialConfig, host.Config);
        Assert.Equal(0, host.Executions);
        Assert.Equal([true], host.LoggedReports); // 明細はトレイの確認と同じく gui.log にも書く
    }

    [Fact]
    public async Task CLIの確認は管理画面の確認を古くしない()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);
        var gui = (await Run(jobs, ReapplyJobKind.Plan)).Report!;

        await Run(jobs, ReapplyJobKind.PlanForCli);

        Assert.True(jobs.HasLastPlan);
        var applied = await Run(jobs, ReapplyJobKind.Apply, plan: gui);
        Assert.False(applied.IsStale);
        Assert.False(applied.Report!.DryRun);
        Assert.Equal(1, host.Executions);
    }

    [Fact]
    public async Task CLIの確認で番組と放送局の設定を読めなければ理由を返す()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")], SettingsError = "programs.json の JSON が壊れています" };

        var planned = await Run(new ReapplyJobs(host), ReapplyJobKind.PlanForCli);

        Assert.Null(planned.Report);
        Assert.Contains("programs.json の JSON が壊れています", planned.Error);
        Assert.False(planned.IsStale);
    }

    [Fact]
    public async Task CLIの確認は直近の設定リロードの失敗では止めない()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")], LastReloadError = "stations.json を読めません" };

        var planned = await Run(new ReapplyJobs(host), ReapplyJobKind.PlanForCli);

        Assert.NotNull(planned.Report);
    }

    // ---- CLI の実行 ----

    [Fact]
    public async Task 予定IDが一致すれば設定リロードしてから実行する()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);
        var planId = (await Run(jobs, ReapplyJobKind.PlanForCli)).PlanId!;

        var applied = await Run(jobs, ReapplyJobKind.ApplyPlanId, planId: planId);

        Assert.False(applied.IsStale);
        Assert.False(applied.Report!.DryRun);
        Assert.Equal(1, host.Executions);
        Assert.Equal(1, host.ConfigLoads);
        Assert.NotSame(host.InitialConfig, host.Config); // 読み直した設定を反映した
        Assert.Same(host.Config, host.LastRunConfig);    // その設定で実行した
        Assert.Equal([true, false], host.LoggedReports); // 確認と実行の明細を gui.log に書く
    }

    [Fact]
    public async Task 予定IDは大文字でも一致とみなす()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);
        var planId = (await Run(jobs, ReapplyJobKind.PlanForCli)).PlanId!;

        var applied = await Run(jobs, ReapplyJobKind.ApplyPlanId, planId: planId.ToUpperInvariant());

        Assert.False(applied.IsStale);
        Assert.Equal(1, host.Executions);
    }

    [Fact]
    public async Task 確認の後に予定が変わったら実行せず_新しい予定とIDを返す()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);
        var planId = (await Run(jobs, ReapplyJobKind.PlanForCli)).PlanId!;
        host.Items = [Item(@"Z:\a.m4a"), Item(@"Z:\b.m4a")]; // 確認の後に programs.json を変えた

        var applied = await Run(jobs, ReapplyJobKind.ApplyPlanId, planId: planId);

        Assert.True(applied.IsStale);
        Assert.Equal(0, host.Executions);
        Assert.NotNull(applied.Report);
        Assert.True(applied.Report!.DryRun);
        Assert.Equal(2, applied.Report.Items.Count);
        Assert.NotEqual(planId, applied.PlanId);
        Assert.Equal(ReapplyPlanId.Of(applied.Report), applied.PlanId);
        Assert.Equal([true, true], host.LoggedReports); // 確認と、変わった後の新しい予定の両方を gui.log に書く

        // 新しい予定 ID で頼み直せば実行する。
        var retried = await Run(jobs, ReapplyJobKind.ApplyPlanId, planId: applied.PlanId);
        Assert.False(retried.IsStale);
        Assert.Equal(1, host.Executions);
    }

    [Fact]
    public async Task 設定を読めなければ実行しない()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);
        var planId = (await Run(jobs, ReapplyJobKind.PlanForCli)).PlanId!;
        host.ConfigError = "radirec.json の JSON が壊れています";

        var applied = await Run(jobs, ReapplyJobKind.ApplyPlanId, planId: planId);

        Assert.Null(applied.Report);
        Assert.False(applied.IsStale);
        Assert.Contains("radirec.json の JSON が壊れています", applied.Error);
        Assert.Contains("取り込みは前の設定のまま", applied.Error);
        Assert.Equal(0, host.Executions);
        Assert.Same(host.InitialConfig, host.Config);
    }

    [Fact]
    public async Task 設定を読めない理由の前置きは_1回だけ()
    {
        // 読み込みの戻り値は前置きを含む(ConfigLoading.TryLoad)。reload の拒否と同じく、そのまま出す(#218)。
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);
        var planId = (await Run(jobs, ReapplyJobKind.PlanForCli)).PlanId!;
        host.ConfigError = "設定の読み込みに失敗しました: JSON が壊れています";

        var applied = await Run(jobs, ReapplyJobKind.ApplyPlanId, planId: planId);

        Assert.Equal("設定の読み込みに失敗しました: JSON が壊れています(取り込みは前の設定のまま動いています)", applied.Error);
    }

    [Fact]
    public async Task 設定を反映できなければ実行しない()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);
        var planId = (await Run(jobs, ReapplyJobKind.PlanForCli)).PlanId!;
        host.ApplyReloadError = "台帳を開けません";

        var applied = await Run(jobs, ReapplyJobKind.ApplyPlanId, planId: planId);

        Assert.Null(applied.Report);
        Assert.Contains("設定の反映に失敗しました: 台帳を開けません", applied.Error);
        Assert.Equal(0, host.Executions);
    }

    [Fact]
    public async Task CLIの実行は管理画面の直前の確認を消さない()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);
        await Run(jobs, ReapplyJobKind.Plan);
        var planId = (await Run(jobs, ReapplyJobKind.PlanForCli)).PlanId!;

        await Run(jobs, ReapplyJobKind.ApplyPlanId, planId: planId);

        // 直前の確認は残る(実行で設定を読み直したので、管理画面の「適用」は設定が変わったとして古くなる。それは実行の側の結果)。
        Assert.True(jobs.HasLastPlan);
    }

    // ---- 管理画面・トレイの確認と実行(切り出す前と同じふるまい)----

    [Fact]
    public async Task 管理画面の確認のあと実行できるのは一度だけ()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);
        var plan = (await Run(jobs, ReapplyJobKind.Plan)).Report!;

        var first = await Run(jobs, ReapplyJobKind.Apply, plan: plan);
        var second = await Run(jobs, ReapplyJobKind.Apply, plan: plan);

        Assert.False(first.IsStale);
        Assert.True(second.IsStale);
        Assert.Equal(1, host.Executions);
        Assert.Null(first.PlanId);
    }

    [Fact]
    public async Task 管理画面の確認のあと予定が変わったら実行しない()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);
        var plan = (await Run(jobs, ReapplyJobKind.Plan)).Report!;
        host.Items = [Item(@"Z:\b.m4a")];

        var applied = await Run(jobs, ReapplyJobKind.Apply, plan: plan);

        Assert.True(applied.IsStale);
        Assert.Null(applied.Report); // 管理画面には新しい予定を返さない(確認からやり直す)
        Assert.Equal(0, host.Executions);
    }

    [Fact]
    public async Task 保存して適用は設定リロードしてから実行する()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")] };
        var jobs = new ReapplyJobs(host);
        var plan = (await jobs.RunAsync(
            new ReapplyJob(ReapplyJobKind.PlanDraft, Draft: new ProgramMasterConfig()), CancellationToken.None)).Report!;

        var applied = await Run(jobs, ReapplyJobKind.ReloadAndApply, plan: plan);

        Assert.False(applied.IsStale);
        Assert.Equal(1, host.ConfigLoads);
        Assert.Equal(1, host.Executions);
    }

    [Fact]
    public async Task 設定リロードの反映に失敗していたら管理画面の保存済みの確認は止める()
    {
        var host = new FakeHost { Items = [Item(@"Z:\a.m4a")], LastReloadError = "stations.json を読めません" };

        var planned = await Run(new ReapplyJobs(host), ReapplyJobKind.Plan);

        Assert.Null(planned.Report);
        Assert.Contains("stations.json を読めません", planned.Error);
    }

    private sealed class FakeHost : IReapplyJobHost
    {
        public FakeHost() => Config = InitialConfig;

        public RadirecConfig InitialConfig { get; } = new() { OutputRoot = @"Z:\out" };

        /// <summary>今 dry-run すると出る明細(台帳と programs.json の今の状態)。</summary>
        public List<ReapplyItem> Items { get; set; } = [];

        public string? ConfigError { get; set; }
        public string? ApplyReloadError { get; set; }
        public string? SettingsError { get; init; }
        public int ConfigLoads { get; private set; }
        public int Executions { get; private set; }
        public RadirecConfig? LastRunConfig { get; private set; }

        public RadirecConfig Config { get; private set; }

        public string? LastReloadError { get; set; }

        public bool TryLoadConfig(out RadirecConfig? config, out string? error)
        {
            ConfigLoads++;
            error = ConfigError;
            config = ConfigError is null ? new RadirecConfig { OutputRoot = @"Z:\out" } : null;
            return ConfigError is null;
        }

        public void ApplyReload(RadirecConfig config)
        {
            if (ApplyReloadError is { } error)
            {
                LastReloadError = error;
                return;
            }

            LastReloadError = null;
            Config = config;
        }

        public ReapplySettings LoadSettings(RadirecConfig config) => SettingsError is { } error
            ? throw new InvalidDataException(error)
            : new ReapplySettings(new StationMasterConfig(), new ProgramMasterConfig());

        public StationMasterConfig LoadStations(RadirecConfig config) => new();

        public Task<(ReapplyReport? Report, string? Error)> RunServiceAsync(
            RadirecConfig config, ReapplySettings settings, bool dryRun, CancellationToken cancellationToken)
        {
            if (!dryRun)
            {
                Executions++;
                LastRunConfig = config;
            }

            var report = new ReapplyReport { Items = [.. Items], DryRun = dryRun, UnintegratedSiblings = [] };
            return Task.FromResult<(ReapplyReport?, string?)>((report, null));
        }

        /// <summary>gui.log に書いた明細(dry-run なら true、実行なら false)。</summary>
        public List<bool> LoggedReports { get; } = [];

        public void LogReport(ReapplyReport report) => LoggedReports.Add(report.DryRun);

        public void LogWarning(string message)
        {
        }

        public void LogError(Exception exception, string message)
        {
        }
    }
}
