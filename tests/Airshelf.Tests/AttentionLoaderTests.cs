using Airshelf.Core.Ledger;
using Airshelf.Hosting;

namespace Airshelf.Tests;

// 要対応を台帳とディスクの設定ファイルから計算する(#155)。台帳は実物の SQLite、設定は一時フォルダのファイル。
public sealed class AttentionLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("radirec-attention-loader-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string LedgerPath => Path.Combine(_dir, "ledger.db");
    private string ProgramsPath => Path.Combine(_dir, "programs.json");

    private void Ingested(params string[] folders)
    {
        using var ledger = new SqliteLedger(LedgerPath);
        foreach (var folder in folders)
        {
            ledger.Record(new LedgerEntry
            {
                IdentityKey = $"TBSラジオ|{folder}",
                Station = "TBSラジオ",
                Program = folder,
                BroadcastDate = new DateOnly(2024, 9, 23),
                SourcePath = Path.Combine(_dir, "radio", "TBSラジオ", folder, $"[TBSラジオ]{folder}(TimeFree)_20240923083000.m4a"),
                OutputPath = Path.Combine(_dir, "out", folder + ".m4a"),
                RuleVersion = "1",
                UsedFallback = true,
                ImportedAtUtc = DateTimeOffset.UnixEpoch,
            });
        }
    }

    [Fact]
    public void Computes_from_the_ledger_and_programs_file_without_touching_sources()
    {
        Ingested("X(1)", "X(2)"); // 取込元のファイルは置かない
        File.WriteAllText(ProgramsPath, """{ "Programs": [] }""");

        var items = AttentionLoader.Load(LedgerPath, stationsFile: null, ProgramsPath, ["TimeFree"]);

        Assert.Equal(["X(1)", "X(2)"], Assert.Single(items.UnintegratedSiblings).Group.Folders);
    }

    [Fact]
    public void Missing_ledger_and_programs_file_are_empty()
    {
        var items = AttentionLoader.Load(LedgerPath, stationsFile: null, ProgramsPath, ["TimeFree"]);

        Assert.Empty(items.UnintegratedSiblings);
        Assert.False(File.Exists(LedgerPath)); // 読むだけで作らない
    }

    [Fact]
    public void Programs_file_unused_lists_no_siblings()
    {
        Ingested("X(1)", "X(2)");

        var items = AttentionLoader.Load(LedgerPath, stationsFile: null, programsFile: "", ["TimeFree"]);

        Assert.Empty(items.UnintegratedSiblings);
    }

    [Fact]
    public void Lists_failures_from_the_ledger_and_drift_from_the_programs_file()
    {
        Ingested("X(1)", "X(2)"); // 台帳は統合設定の無いときの記録(アルバム X(1) / X(2))
        var at = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        using (var ledger = new SqliteLedger(LedgerPath))
            ledger.Record(Path.Combine(_dir, "radio", "bad.m4a"), new FileFingerprint(1, 2), "bad.m4a を読めません", at);
        File.WriteAllText(ProgramsPath, """
            { "Programs": [ { "Match": { "Station": "TBSラジオ", "Folders": [ "X(1)", "X(2)" ] },
                              "Canonical": { "Album": "X" } } ] }
            """);

        var items = AttentionLoader.Load(LedgerPath, stationsFile: null, ProgramsPath, ["TimeFree"]);

        var failure = Assert.Single(items.UnresolvedFailures);
        Assert.Equal("bad.m4a を読めません", failure.Reason);
        Assert.Equal(at, failure.FailedAtUtc);
        Assert.Equal(["X(1)", "X(2)"], items.SettingsDrift.Select(d => d.FromAlbum));
        Assert.All(items.SettingsDrift, d => Assert.Equal("X", d.ToAlbum));
    }

    [Fact]
    public void Broken_programs_file_throws()
    {
        File.WriteAllText(ProgramsPath, "{ \"Programs\": [ ");

        Assert.ThrowsAny<Exception>(() => AttentionLoader.Load(LedgerPath, null, ProgramsPath, ["TimeFree"]));
    }

    [Fact]
    public void Fails_instead_of_empty_when_the_ledger_folder_is_unreachable()
    {
        // 親フォルダが無い(NAS に届かない)なら空の台帳として計算しない(#216)。ファイルだけ無いときは空(上のテスト)。
        var unreachable = Path.Combine(_dir, "nas-down", "ledger.db");
        Assert.ThrowsAny<IOException>(
            () => AttentionLoader.Load(unreachable, stationsFile: null, programsFile: null, ["TimeFree"]));
    }
}
