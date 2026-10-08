using Airshelf.Core.Ingestion;
using Airshelf.Core.Reapply;
using Airshelf.Gui.Core.Management;
using Airshelf.Hosting;
using Airshelf.Hosting.Control;
using static Airshelf.Tests.ServiceStatusTextTests;

namespace Airshelf.Tests;

// 管理画面の設定ページと、新規取込のトーストの切り替え(#152・ADR-0019)。文言の ID は docs/mockups/management-window.md。
public sealed class SettingsPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"airshelf-settings-{Guid.NewGuid():N}");
    private readonly List<string> _warnings = [];

    public SettingsPageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string SettingsPath => Path.Combine(_dir, GuiSettingsStore.FileName);

    private GuiSettingsStore Store() => new(SettingsPath, _warnings.Add);

    // ---------- 新規取込のトーストを止めても、失敗は止まらない ----------

    private sealed class CountingSummaryPublisher : IIngestSummaryPublisher
    {
        public int Calls { get; private set; }

        public Task<string?> PublishAsync(IngestSummary summary, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class CountingFailurePublisher : IIngestFailurePublisher
    {
        public int Calls { get; private set; }

        public Task PublishAsync(IngestFailureSignal signal, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static IngestSummary Summary() => new() { TotalImported = 1, Truncated = false, Albums = [] };

    private static IngestFailureSignal Failure() => new() { FailedCount = 1, Truncated = false, Files = [] };

    [Fact]
    public async Task 新規取込のトーストを止めても_失敗のトーストは出て_失敗のストアも使う()
    {
        // トレイ(Program.BuildHost)と同じ組み方: 成功要約だけを設定で止め、失敗の publisher はそのまま渡す。
        var setting = IngestToastSetting.Fixed(false);
        var toast = new CountingSummaryPublisher();
        var failureToast = new CountingFailurePublisher();

        var (summary, failure, useStore) = CompositionRoot.SelectPublishers(
            notify: true, dryRun: false,
            injectedSummary: new GatedIngestSummaryPublisher(toast, () => setting.Enabled),
            injectedFailure: failureToast);

        await summary.PublishAsync(Summary(), TestContext.Current.CancellationToken);
        await failure.PublishAsync(Failure(), TestContext.Current.CancellationToken);

        Assert.Equal(0, toast.Calls);        // 新規取込のトーストは出ない
        Assert.Equal(1, failureToast.Calls); // 失敗のトーストは出る
        Assert.True(useStore);               // 失敗のストアにも記録される(ADR-0011 の de-dup)
    }

    [Fact]
    public async Task 切り替えは次のシグナルからすぐ効く()
    {
        var enabled = true;
        var toast = new CountingSummaryPublisher();
        var gate = new GatedIngestSummaryPublisher(toast, () => enabled);

        await gate.PublishAsync(Summary(), TestContext.Current.CancellationToken);
        enabled = false;
        await gate.PublishAsync(Summary(), TestContext.Current.CancellationToken);
        enabled = true;
        await gate.PublishAsync(Summary(), TestContext.Current.CancellationToken);

        Assert.Equal(2, toast.Calls);
    }

    // ---------- 管理画面用の設定ファイル(gui-settings.json)と後方互換 ----------

    [Fact]
    public void 値が無ければ旧設定NotificationsEnabledを初期値にして書く()
    {
        var setting = IngestToastSetting.Load(Store(), legacyNotificationsEnabled: false, _warnings.Add);

        Assert.False(setting.Enabled);
        Assert.False(Store().Load().IngestToastsEnabled); // 以後は設定ファイルの値だけを使う
    }

    [Fact]
    public void 設定ファイルに値があれば旧設定は見ない()
    {
        Store().Save(new GuiSettings { IngestToastsEnabled = true });

        var setting = IngestToastSetting.Load(Store(), legacyNotificationsEnabled: false, _warnings.Add);

        Assert.True(setting.Enabled);
    }

    [Fact]
    public void airshelfjsonを読めないときは書かずに出すで始める()
    {
        var setting = IngestToastSetting.Load(Store(), legacyNotificationsEnabled: null, _warnings.Add);

        Assert.True(setting.Enabled);
        Assert.False(File.Exists(SettingsPath)); // 旧設定が分かるまで値を固めない
    }

    [Fact]
    public void 壊れた設定ファイルは記録して旧設定から引き継ぐ()
    {
        File.WriteAllText(SettingsPath, "{ not json");

        var setting = IngestToastSetting.Load(Store(), legacyNotificationsEnabled: false, _warnings.Add);

        Assert.False(setting.Enabled);
        Assert.Contains(_warnings, w => w.Contains(GuiSettingsStore.FileName));
        Assert.False(Store().Load().IngestToastsEnabled); // 書き直された
    }

    [Fact]
    public void 切り替えると保存される()
    {
        var setting = IngestToastSetting.Load(Store(), legacyNotificationsEnabled: true, _warnings.Add);

        setting.Set(false);

        Assert.False(setting.Enabled);
        Assert.False(IngestToastSetting.Load(Store(), legacyNotificationsEnabled: true, _warnings.Add).Enabled);
    }

    [Fact]
    public void 設定ファイルはコメントと知らないキーがあっても読める()
    {
        File.WriteAllText(SettingsPath, """
            {
              // 管理画面が書く
              "IngestToastsEnabled": false,
              "Future": 1,
            }
            """);

        Assert.False(Store().Load().IngestToastsEnabled);
    }

    // ---------- 設定ページ ----------

    private sealed class FakeControl : IServiceControl
    {
        public ServiceStatusSnapshot Status { get; set; } = Snapshot();
        public ReloadResult ReloadResult { get; set; } = ReloadResult.Ok();
        public int Reloads { get; private set; }

        public void RequestRescan() { }
        public Task<ScanRunResult> RunScanAsync() => throw new NotSupportedException();
        public ServiceStatusSnapshot GetStatus() => Status;

        public ReloadResult RequestReload()
        {
            Reloads++;
            return ReloadResult;
        }

        public Task<ReapplyResult> PlanReapplyAsync() => throw new NotSupportedException();
        public Task<ReapplyResult> PlanReapplyAsync(Airshelf.Core.Configuration.ProgramMasterConfig programs) => throw new NotSupportedException();
        public Task<ReapplyResult> ApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
        public Task<ReapplyResult> PlanReapplyForCliAsync() => throw new NotSupportedException();
        public Task<ReapplyResult> ApplyReapplyPlanAsync(string planId) => throw new NotSupportedException();
        public Task<ReapplyResult> ReloadAndApplyReapplyAsync(ReapplyReport plan) => throw new NotSupportedException();
    }

    private sealed class FakeActions : ISettingsPageActions
    {
        public Dictionary<string, string> Files { get; } = [];
        public Exception? ThrowOnRead { get; set; }
        public bool ConfirmAnswer { get; set; } = true;
        public int Confirms { get; private set; }
        public int Restarts { get; private set; }
        public List<(ConfigFile File, string Path)> Opened { get; } = [];
        public SettingsFileOpenResult OpenResult { get; set; } = new(true, null);

        public Task<SettingsFileOpenResult> OpenAsync(ConfigFile file, string path)
        {
            Opened.Add((file, path));
            return Task.FromResult(OpenResult);
        }

        public string? ReadText(string path) =>
            ThrowOnRead is { } ex ? throw ex : Files.GetValueOrDefault(path);

        public bool ConfirmReload(string message)
        {
            Assert.Equal(SettingsPageViewModel.ReloadConfirm, message); // W-309
            Confirms++;
            return ConfirmAnswer;
        }

        public void Restart() => Restarts++;
    }

    private string ConfigPath => Path.Combine(_dir, "airshelf.json");
    private string StationsPath => Path.Combine(_dir, "stations.json");
    private string ProgramsPath => Path.Combine(_dir, "programs.json");

    private AirshelfConfig Config() => new() { StationsFile = StationsPath, ProgramsFile = ProgramsPath };

    private SettingsPageViewModel Page(
        FakeControl? control, FakeActions actions, AirshelfConfig? config = null, string? error = null,
        IngestToastSetting? toast = null, string? startError = null) =>
        new(control, ConfigPath, config, error, toast ?? IngestToastSetting.Fixed(true), actions, _warnings.Add, startError);

    [Fact]
    public void 縮退時は縮退パネルを出す()
    {
        var page = Page(control: null, new FakeActions(), config: null, error: "設定の読み込みに失敗しました: x");

        Assert.True(page.IsDegraded);
        Assert.False(page.IsNormal);
        Assert.Equal("設定の読み込みに失敗しました: x", page.DegradedReason); // W-402
        Assert.Equal(ConfigPath, page.ConfigPath);
        Assert.True(page.OpenAirshelfCommand.CanExecute(null));   // airshelf.json は開ける(直すため)
        Assert.False(page.OpenStationsCommand.CanExecute(null)); // W-405(場所が分からない)
    }

    [Fact]
    public void 縮退パネルの再読み込みして再起動()
    {
        var actions = new FakeActions();
        var page = Page(control: null, actions);

        page.RestartCommand.Execute(null);

        Assert.Equal(1, actions.Restarts);
    }

    // ---------- 起動の失敗のパネル(W-104 のとき。#168) ----------

    [Fact]
    public async Task 取り込みを起動できないときは_先頭のパネルで原因を出し_ファイルを開いて再起動できる()
    {
        var actions = new FakeActions();
        const string reason = "programs.json を読めません(JSON が壊れています): '}' is invalid";
        var page = Page(control: null, actions, Config(), startError: reason);

        Assert.True(page.IsStartFailed);
        Assert.True(page.IsNormal);                       // ページを置き換えない(通常の設定ページも出す)
        Assert.False(page.IsDegraded);
        Assert.Equal(reason, page.StartFailedReason);     // W-407

        // W-409:原因になりうるファイルを、起動時に読めた設定の場所で開ける。
        Assert.True(page.OpenProgramsCommand.CanExecute(null));
        await page.OpenProgramsCommand.ExecuteAsync(null);
        await page.OpenStationsFromPanelCommand.ExecuteAsync(null);
        Assert.Equal([(ConfigFile.Programs, ProgramsPath), (ConfigFile.Stations, StationsPath)], actions.Opened);

        page.RestartCommand.Execute(null);
        Assert.Equal(1, actions.Restarts);
    }

    [Fact]
    public async Task programsjsonを見本から作っても中身は読まない()
    {
        var actions = new FakeActions { OpenResult = new(true, "programs.json が無かったので見本から作りました") };
        var page = Page(control: null, actions, Config(), startError: "x");

        await page.OpenProgramsCommand.ExecuteAsync(null);

        Assert.Equal("programs.json が無かったので見本から作りました", page.StartFailedPanel.Message); // W-308
        Assert.False(page.StartFailedPanel.IsMessageWarning);
        Assert.Null(page.Programs.Content); // 中身は番組ページで見る(W-313)
    }

    [Fact]
    public async Task パネルでstationsjsonを開いた結果は_パネルにだけ出る()
    {
        var actions = new FakeActions { OpenResult = new(false, "stations.json を開けませんでした(gui.log 参照)") };
        var page = Page(control: null, actions, Config(), startError: "x");

        await page.OpenStationsFromPanelCommand.ExecuteAsync(null);

        Assert.Equal("stations.json を開けませんでした(gui.log 参照)", page.StartFailedPanel.Message);
        Assert.True(page.StartFailedPanel.IsMessageWarning);
        Assert.Null(page.Stations.Message); // 下の stations.json の段には出さない(2 か所に出ない)
    }

    [Fact]
    public void 起動できたときと縮退時は起動の失敗のパネルを出さない()
    {
        var running = Page(new FakeControl(), new FakeActions(), Config());
        Assert.False(running.IsStartFailed);
        Assert.Equal("", running.StartFailedReason);

        // airshelf.json を読めない(W-105)なら縮退パネルが代わりに出る。
        var degraded = Page(control: null, new FakeActions(), config: null, error: "設定の読み込みに失敗しました: x", startError: "y");
        Assert.False(degraded.IsStartFailed);
        Assert.False(degraded.OpenProgramsCommand.CanExecute(null));
    }

    [Fact]
    public void programsjsonを使わない設定ならprogramsjsonは開けない()
    {
        var page = Page(control: null, new FakeActions(), new AirshelfConfig { StationsFile = StationsPath, ProgramsFile = "" }, startError: "x");

        Assert.False(page.OpenProgramsCommand.CanExecute(null));
        Assert.True(page.OpenStationsCommand.CanExecute(null));
    }

    [Fact]
    public async Task ページを開くと中身を読み_秘密は伏せる()
    {
        var actions = new FakeActions();
        actions.Files[ConfigPath] = """{ "JellyfinApiKey": "abc123", "UpdateToken": "", "RadioRoot": "I:\\radio" }""";
        actions.Files[StationsPath] = """{ "Stations": [] }""";
        var control = new FakeControl { Status = Snapshot() with { StationsFile = StationsPath, ProgramsFile = ProgramsPath } };
        var page = Page(control, actions, Config());

        await page.RefreshAsync();

        Assert.Equal("""{ "JellyfinApiKey": (設定済み・表示しません), "UpdateToken": "", "RadioRoot": "I:\\radio" }""", page.Airshelf.Content);
        Assert.Equal("""{ "Stations": [] }""", page.Stations.Content);
        Assert.Equal(StationsPath, page.Stations.Path);
        Assert.Equal(SettingsPageViewModel.ProgramsNoteUsed, page.ProgramsNote); // W-313
    }

    [Fact]
    public async Task 使わないファイルとまだ無いファイルと読めないファイル()
    {
        var actions = new FakeActions();
        actions.Files[ConfigPath] = "{}";
        var control = new FakeControl { Status = Snapshot() with { StationsFile = "", ProgramsFile = "" } };
        var page = Page(control, actions, Config());

        await page.RefreshAsync();

        Assert.Null(page.Stations.Path);
        Assert.Equal(SettingsPageViewModel.FileNotUsed, page.Stations.Note);             // W-307
        Assert.False(page.OpenStationsCommand.CanExecute(null));
        Assert.Equal(SettingsPageViewModel.ProgramsNoteUnused, page.ProgramsNote);       // W-212

        control.Status = Snapshot() with { StationsFile = StationsPath, ProgramsFile = ProgramsPath };
        await page.RefreshAsync();
        Assert.Equal(SettingsPageViewModel.FileMissing("stations.json"), page.Stations.Note); // W-317

        actions.ThrowOnRead = new IOException("locked");
        await page.RefreshAsync();
        Assert.Equal(SettingsPageViewModel.ReadFailed("airshelf.json"), page.Airshelf.Note); // W-315
        Assert.Null(page.Airshelf.Content);
    }

    [Fact]
    public async Task ワーカーが無いときは起動時の設定から場所を求め_再読み込みは押せない()
    {
        var actions = new FakeActions();
        var page = Page(control: null, actions, Config());

        await page.RefreshAsync();

        Assert.Equal(StationsPath, page.Stations.Path);
        Assert.False(page.CanReload);
        Assert.Equal(SettingsPageViewModel.ReloadUnavailable, page.ReloadNote); // W-316
    }

    [Fact]
    public void 異常終了した取り込みには再読み込みを頼まない()
    {
        var page = Page(new FakeControl { Status = Snapshot(running: false) }, new FakeActions(), Config());

        Assert.False(page.ReloadCommand.CanExecute(null));
        Assert.Equal(SettingsPageViewModel.ReloadUnavailable, page.ReloadNote);
    }

    [Fact]
    public async Task 再読み込みは確認してから頼み_結果を画面に出す()
    {
        var actions = new FakeActions { ConfirmAnswer = false };
        var control = new FakeControl();
        var page = Page(control, actions, Config());
        Assert.Null(page.ReloadNote);

        await page.ReloadCommand.ExecuteAsync(null);
        Assert.Equal(0, control.Reloads); // キャンセル

        actions.ConfirmAnswer = true;
        await page.ReloadCommand.ExecuteAsync(null);
        Assert.Equal(1, control.Reloads);
        Assert.Equal(SettingsPageViewModel.ReloadAccepted, page.Airshelf.Message); // W-310
        Assert.False(page.Airshelf.IsMessageWarning);

        control.ReloadResult = ReloadResult.Failed("設定の読み込みに失敗しました: x");
        await page.ReloadCommand.ExecuteAsync(null);
        Assert.Equal("設定リロードは拒否されました: 設定の読み込みに失敗しました: x", page.Airshelf.Message);
        Assert.True(page.Airshelf.IsMessageWarning);
    }

    [Fact]
    public async Task ファイルを開いた結果を段の中に出す()
    {
        var actions = new FakeActions { OpenResult = new(false, "stations.json を開けませんでした(gui.log 参照)") };
        var page = Page(new FakeControl { Status = Snapshot() with { StationsFile = StationsPath } }, actions, Config());

        await page.OpenStationsCommand.ExecuteAsync(null);

        Assert.Equal((ConfigFile.Stations, StationsPath), actions.Opened.Single());
        Assert.Equal("stations.json を開けませんでした(gui.log 参照)", page.Stations.Message); // W-308
        Assert.True(page.Stations.IsMessageWarning);
    }

    [Fact]
    public void トーストの切り替えは保存され_失敗したら元に戻す()
    {
        var setting = IngestToastSetting.Load(Store(), legacyNotificationsEnabled: true, _warnings.Add);
        var page = Page(new FakeControl(), new FakeActions(), Config(), toast: setting);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.IngestToastsEnabled = false;
        Assert.False(setting.Enabled);
        Assert.False(Store().Load().IngestToastsEnabled);
        Assert.Null(page.ToastMessage);

        // 保存できない(設定ファイルの場所がフォルダになっている)
        File.Delete(SettingsPath);
        Directory.CreateDirectory(SettingsPath);
        page.IngestToastsEnabled = true;

        Assert.False(page.IngestToastsEnabled); // 元のまま
        Assert.Equal(SettingsPageViewModel.ToastSaveFailed, page.ToastMessage); // W-314
        Assert.Contains(nameof(SettingsPageViewModel.IngestToastsEnabled), changed); // チェックを戻す通知
    }

    [Theory]
    [InlineData("""{"JellyfinApiKey":"k"}""", """{"JellyfinApiKey":(設定済み・表示しません)}""")]
    [InlineData("""{ "updatetoken" : "a\"b" }""", """{ "updatetoken" : (設定済み・表示しません) }""")]
    [InlineData("""{ "JellyfinApiKey": "" }""", """{ "JellyfinApiKey": "" }""")] // 空は未設定と分かるようにそのまま
    [InlineData("""{ "JellyfinUrl": "http://x" }""", """{ "JellyfinUrl": "http://x" }""")]
    public void 秘密を伏せる(string json, string expected) =>
        Assert.Equal(expected, SettingsPageViewModel.MaskSecrets(json));

    [Fact]
    public async Task 設定ページを開くと読み直す()
    {
        var actions = new FakeActions();
        actions.Files[ConfigPath] = "{}";
        var settings = Page(new FakeControl(), actions, Config());
        var vm = new ManagementViewModel(null, null, null, new NoPathActions(), _warnings.Add, settings: settings);
        Assert.Null(settings.Airshelf.Content); // 開くまでは読まない

        vm.CurrentPage = ManagementPage.Settings;

        // 読み込みはワーカースレッドで走る(ページの切り替えは待たない)ので、終わるのを待つ。
        for (var i = 0; i < 200 && settings.Airshelf.Content is null; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Same(settings, vm.Settings);
        Assert.Equal("{}", settings.Airshelf.Content);
    }

    private sealed class NoPathActions : IHistoryPathActions
    {
        public Task<string> RevealAsync(string path) => Task.FromResult("");
        public bool TryCopy(string path) => true;
    }
}
