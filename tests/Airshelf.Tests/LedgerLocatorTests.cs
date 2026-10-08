using Airshelf.Core.Ledger;
using Airshelf.Core.Reapply;
using Airshelf.Gui.Core.Management;
using Airshelf.Hosting;
using Airshelf.Hosting.Control;

namespace Airshelf.Tests;

// 管理画面が読む台帳の場所(#196)。ワーカーがあれば今の状態から読むたびに決め直し、設定リロードで LedgerPath を変えたら
// 次の読み込みから新しい台帳を読む。
public sealed class LedgerLocatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("airshelf-ledger-locator-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Follows_the_ledger_path_of_the_current_status()
    {
        var control = new FakeControl { Status = Snapshot("/data/old.db") };
        var locate = LedgerLocator.Create(control, "/data/old.db")!;

        Assert.Equal("/data/old.db", locate());

        control.Status = Snapshot("/data/new.db"); // 設定リロードで LedgerPath が変わった
        Assert.Equal("/data/new.db", locate());
    }

    [Fact]
    public void Without_a_worker_uses_the_startup_ledger_path_W104()
    {
        var locate = LedgerLocator.Create(control: null, "/data/startup.db")!;

        Assert.Equal("/data/startup.db", locate());
    }

    [Fact]
    public void Without_readable_settings_there_is_no_ledger_W150()
    {
        Assert.Null(LedgerLocator.Create(control: null, startupLedgerPath: null));
    }

    [Fact]
    public void Status_without_a_ledger_path_falls_back_to_the_startup_path()
    {
        var control = new FakeControl { Status = Snapshot("") };

        Assert.Equal("/data/startup.db", LedgerLocator.Create(control, "/data/startup.db")!());
    }

    [Fact]
    public void Status_failure_propagates_to_the_reader()
    {
        var control = new FakeControl { Throw = new InvalidOperationException("状態を読めない") };
        var locate = LedgerLocator.Create(control, "/data/startup.db")!;

        Assert.Throws<InvalidOperationException>(() => locate());
    }

    [Fact]
    public void Resolve_takes_the_ledger_and_the_settings_files_from_the_same_status()
    {
        // ワーカーは 3 つを 1 回の状態の差し替えで変える。1 回の計算では、その 1 つの状態から 3 つをそろえて取る。
        var status = Snapshot("/new/ledger.db") with { StationsFile = "/new/stations.json", ProgramsFile = "/new/programs.json" };

        var at = LedgerLocator.Resolve(status, "/old/ledger.db", "/old/stations.json", "/old/programs.json");

        Assert.Equal(new ManagementDataLocations("/new/ledger.db", "/new/stations.json", "/new/programs.json"), at);
    }

    [Fact]
    public void Resolve_without_a_worker_takes_all_three_from_the_startup_settings_W104()
    {
        var at = LedgerLocator.Resolve(null, "/old/ledger.db", "/old/stations.json", "/old/programs.json");

        Assert.Equal(new ManagementDataLocations("/old/ledger.db", "/old/stations.json", "/old/programs.json"), at);
    }

    [Fact]
    public void Resolve_keeps_empty_settings_files_as_unused_but_not_an_empty_ledger()
    {
        // programs.json / stations.json の空は「使わない設定」なのでそのまま。台帳の空は起動時の台帳へ戻る。
        var status = Snapshot("") with { StationsFile = "", ProgramsFile = "" };

        var at = LedgerLocator.Resolve(status, "/old/ledger.db", "/old/stations.json", "/old/programs.json");

        Assert.Equal(new ManagementDataLocations("/old/ledger.db", "", ""), at);
    }

    [Fact]
    public void After_reload_the_attention_is_computed_from_the_new_ledger()
    {
        var oldLedger = Path.Combine(_dir, "old.db");
        var newLedger = Path.Combine(_dir, "new.db");
        RecordFailure(oldLedger, "old.m4a");
        RecordFailure(newLedger, "new.m4a");
        var control = new FakeControl { Status = Snapshot(oldLedger) };
        var locate = LedgerLocator.Create(control, oldLedger)!;

        var before = AttentionLoader.Load(locate(), stationsFile: null, programsFile: null, ["TimeFree"]);
        control.Status = Snapshot(newLedger);
        var after = AttentionLoader.Load(locate(), stationsFile: null, programsFile: null, ["TimeFree"]);

        Assert.EndsWith("old.m4a", Assert.Single(before.UnresolvedFailures).SourcePath);
        Assert.EndsWith("new.m4a", Assert.Single(after.UnresolvedFailures).SourcePath);
    }

    private void RecordFailure(string ledgerPath, string fileName)
    {
        using var ledger = new SqliteLedger(ledgerPath);
        ledger.Record(Path.Combine(_dir, fileName), new FileFingerprint(1, 1), "壊れている", DateTimeOffset.UnixEpoch);
    }

    private static ServiceStatusSnapshot Snapshot(string ledgerPath) => new()
    {
        Version = "test",
        StartedUtc = DateTimeOffset.UnixEpoch,
        RadioRoot = "/radio",
        Watching = true,
        LedgerPath = ledgerPath,
    };

    private sealed class FakeControl : IServiceControl
    {
        public ServiceStatusSnapshot Status { get; set; } = Snapshot("");
        public Exception? Throw { get; set; }

        public void RequestRescan() { }
        public Task<ScanRunResult> RunScanAsync() => throw new NotSupportedException();
        public ServiceStatusSnapshot GetStatus() => Throw is { } ex ? throw ex : Status;
        public ReloadResult RequestReload() => ReloadResult.Ok();
        public Task<ReapplyResult> PlanReapplyAsync() => throw new NotSupportedException();
        public Task<ReapplyResult> PlanReapplyAsync(Airshelf.Core.Configuration.ProgramMasterConfig programs) => throw new NotSupportedException();
        public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
        public Task<ReapplyResult> PlanReapplyForCliAsync() => throw new NotSupportedException();
        public Task<ReapplyResult> ApplyReapplyPlanAsync(string planId) => throw new NotSupportedException();
        public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
    }
}
