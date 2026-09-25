using Radirec.Core.Ledger;
using Radirec.Hosting;

namespace Radirec.Tests;

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
    public void Broken_programs_file_throws()
    {
        File.WriteAllText(ProgramsPath, "{ \"Programs\": [ ");

        Assert.ThrowsAny<Exception>(() => AttentionLoader.Load(LedgerPath, null, ProgramsPath, ["TimeFree"]));
    }
}
