using System.Text.Json;
using Radirec.Cli.Core;
using Radirec.Cli.Core.Commands;
using Radirec.Core.Ledger;
using Radirec.Gui.Core;
using Radirec.Hosting;
using Spectre.Console;

namespace Radirec.Tests;

/// <summary>
/// <c>radirec history</c>(#182)と、管理画面と共有する台帳の読み出し(<see cref="IngestHistoryReader"/>)。
/// 台帳は実物の SQLite を一時フォルダに作る。
/// </summary>
public sealed class HistoryCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("radirec-history-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string LedgerPath => Path.Combine(_dir, "ledger.db");

    // ---- 台帳の読み出し(管理画面と共有) ----

    [Fact]
    public void 台帳がまだ無ければ空で_ファイルを作らない()
    {
        Assert.Empty(IngestHistoryReader.Load(LedgerPath));
        Assert.False(File.Exists(LedgerPath));
    }

    [Fact]
    public void 台帳のあるフォルダに届かなければ空とせず失敗にする()
    {
        var unreachable = Path.Combine(_dir, "nas-down", "ledger.db");
        Assert.ThrowsAny<IOException>(() => IngestHistoryReader.Load(unreachable));
    }
    [Fact]
    public void 台帳の記録を新しい順に返す()
    {
        Record(Entry("古い", minutes: 0), Entry("新しい", minutes: 10), Entry("中", minutes: 5));

        Assert.Equal(["新しい", "中", "古い"], IngestHistoryReader.Load(LedgerPath).Select(e => e.Program));
    }

    // ---- 絞り込み ----

    [Fact]
    public void 既定の件数は管理画面のホームと同じ()
    {
        Assert.Equal(Radirec.Gui.Core.Management.HomeViewModel.RecentCount, HistoryCommand.DefaultLimit);
    }

    [Fact]
    public void limitの件数まで新しい順に出す_0は全件()
    {
        var entries = Enumerable.Range(0, 8).Select(i => Entry($"p{i}", minutes: -i)).ToList(); // 新しい順

        Assert.Equal(["p0", "p1", "p2"], HistoryCommand.Select(entries, limit: 3, since: null).Entries.Select(e => e.Program));
        Assert.Equal(8, HistoryCommand.Select(entries, limit: 0, since: null).Entries.Count);
    }

    [Fact]
    public void sinceは取込日時がそれ以降のものだけに絞ってからlimitを当てる()
    {
        var entries = Enumerable.Range(0, 6).Select(i => Entry($"p{i}", minutes: -10 * i)).ToList(); // 0, -10, …, -50 分

        var result = HistoryCommand.Select(entries, limit: 0, since: Base.AddMinutes(-25));
        Assert.Equal(["p0", "p1", "p2"], result.Entries.Select(e => e.Program));

        Assert.Equal(["p0", "p1"], HistoryCommand.Select(entries, limit: 2, since: Base.AddMinutes(-25)).Entries.Select(e => e.Program));
    }

    // CI のローカルは UTC なので、UTC として読む誤りでも通ってしまわないよう、UTC 以外のゾーンで確かめる。
    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");

    [Fact]
    public void sinceの時差の無い値は渡したゾーンの時刻として読む()
    {
        Assert.True(HistoryCommand.TryParseSince("2026-09-01", Tokyo, out var date));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(9)), date);

        Assert.True(HistoryCommand.TryParseSince("2026-09-01T12:00", Tokyo, out var dateTime));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(9)), dateTime);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero), dateTime.ToUniversalTime()); // UTC なら 03:00
    }

    [Theory]
    [InlineData("2026-09-01T12:00+02:00", 2)]
    [InlineData("2026-09-01T12:00-05:00", -5)]
    [InlineData("2026-09-01T12:00Z", 0)]
    public void sinceの時差つきの値はそのまま読む(string value, int offsetHours)
    {
        Assert.True(HistoryCommand.TryParseSince(value, Tokyo, out var since));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(offsetHours)), since);
    }

    [Fact]
    public void sinceの既定はマシンのローカル時刻()
    {
        Assert.True(HistoryCommand.TryParseSince("2026-09-01T12:00", out var since));
        var expected = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(new DateTimeOffset(expected, TimeZoneInfo.Local.GetUtcOffset(expected)), since);
    }

    // ---- コマンドとして ----

    [Fact]
    public async Task jsonは整形前の値を新しい順に出す()
    {
        Record(
            Entry("古い回", minutes: 0),
            Entry("新しい回", minutes: 30) with { BroadcastTime = new TimeOnly(8, 30), PartIndex = 2 });

        var (exitCode, output) = await Run(["history", "--json"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal(CliJson.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("history", root.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);

        var entries = root.GetProperty("result").GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        var first = entries[0];
        Assert.Equal("新しい回", first.GetProperty("program").GetString());
        Assert.Equal("TBSラジオ", first.GetProperty("station").GetString());
        Assert.Equal("2026-06-12", first.GetProperty("broadcastDate").GetString());
        Assert.Equal("08:30:00", first.GetProperty("broadcastTime").GetString());
        Assert.Equal(2, first.GetProperty("part").GetInt32());
        Assert.Equal(Base.AddMinutes(30), first.GetProperty("importedAt").GetDateTimeOffset());
        Assert.Matches(@"T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$", first.GetProperty("importedAt").GetString()); // 時差つき
        Assert.EndsWith("新しい回.m4a", first.GetProperty("outputPath").GetString());
        Assert.EndsWith("新しい回_src.m4a", first.GetProperty("sourcePath").GetString());
        Assert.Equal(JsonValueKind.Null, entries[1].GetProperty("broadcastTime").ValueKind);
    }

    [Fact]
    public async Task limitを付けなければ既定の5件()
    {
        Record(Enumerable.Range(0, 7).Select(i => Entry($"p{i}", minutes: -i)).ToArray());

        var (exitCode, output) = await Run(["history", "--json"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(output);
        var entries = json.RootElement.GetProperty("result").GetProperty("entries");
        Assert.Equal(HistoryCommand.DefaultLimit, entries.GetArrayLength());
        Assert.Equal(5, entries.GetArrayLength());
        Assert.Equal("p0", entries[0].GetProperty("program").GetString());
    }

    [Fact]
    public async Task limit0を付ければ全件()
    {
        Record(Enumerable.Range(0, 7).Select(i => Entry($"p{i}", minutes: -i)).ToArray());

        var (_, output) = await Run(["history", "--limit", "0", "--json"]);

        using var json = JsonDocument.Parse(output);
        Assert.Equal(7, json.RootElement.GetProperty("result").GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task 表は管理画面と同じ放送の表記で出す()
    {
        var entry = Entry("番組A", minutes: 0) with { BroadcastTime = new TimeOnly(8, 30), PartIndex = 2 };
        Record(entry);

        var (exitCode, output) = await Run(["history"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains(IngestHistoryRow.From(entry).Broadcast, output); // 2026-06-12 08:30 (2)
        Assert.Contains("番組A.m4a", output);
        Assert.Contains("新しい順に 1 件", output);
    }

    [Fact]
    public async Task 台帳がまだ無ければ空で0()
    {
        var (exitCode, output) = await Run(["history", "--json"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(0, json.RootElement.GetProperty("result").GetProperty("entries").GetArrayLength());
        Assert.False(File.Exists(LedgerPath));
    }

    [Fact]
    public async Task 台帳のあるフォルダに届かなければ5で_空とは答えない()
    {
        // NAS に届かない UNC と同じく、File.Exists が例外を出さずに false を返す場所(#216)。
        var unreachable = Path.Combine(_dir, "nas-down", "ledger.db");
        var (exitCode, output) = await Run(["history", "--json"], new StubConfigSource(new RadirecConfig { LedgerPath = unreachable }));
        Assert.Equal(ExitCodes.ExternalFailure, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal("history", json.RootElement.GetProperty("command").GetString());
        Assert.Contains(Path.Combine(_dir, "nas-down"), json.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("result").ValueKind);
    }
    [Theory]
    [InlineData("--since", "きのう")]
    [InlineData("--limit", "-1")]
    public async Task 読めない値は使い方の誤りの1(string option, string value)
    {
        var (exitCode, output) = await Run(["history", option, value, "--json"]);

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal("history", json.RootElement.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task 設定を読めなければ1()
    {
        var (exitCode, _) = await Run(["history"], new FailingConfigSource());

        Assert.Equal(ExitCodes.UsageOrConfig, exitCode);
    }

    // ---- 部品 ----

    private static readonly DateTimeOffset Base = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

    private LedgerEntry Entry(string program, int minutes) => new()
    {
        IdentityKey = $"TBSラジオ|{program}|{minutes}",
        Station = "TBSラジオ",
        Program = program,
        BroadcastDate = new DateOnly(2026, 6, 12),
        SourcePath = Path.Combine(_dir, "radio", program + "_src.m4a"),
        OutputPath = Path.Combine(_dir, "out", program + ".m4a"),
        RuleVersion = "1",
        UsedFallback = false,
        ImportedAtUtc = Base.AddMinutes(minutes),
    };

    private void Record(params LedgerEntry[] entries)
    {
        using var ledger = new SqliteLedger(LedgerPath);
        foreach (var entry in entries)
            ledger.Record(entry);
    }

    private Task<(int ExitCode, string Output)> Run(string[] args) =>
        Run(args, new StubConfigSource(new RadirecConfig { LedgerPath = LedgerPath }));

    private static async Task<(int ExitCode, string Output)> Run(string[] args, ICliConfigSource source)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        var exitCode = await RadirecCli.RunAsync(args, "9.9.9-test", console, source);
        return (exitCode, writer.ToString());
    }

    private sealed class StubConfigSource(RadirecConfig config) : ICliConfigSource
    {
        public bool TryLoad(out RadirecConfig? loaded, out string? error)
        {
            loaded = config;
            error = null;
            return true;
        }
    }

    private sealed class FailingConfigSource : ICliConfigSource
    {
        public bool TryLoad(out RadirecConfig? config, out string? error)
        {
            config = null;
            error = "radirec.json を読めません";
            return false;
        }
    }
}
