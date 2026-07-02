using Radirec.Core.Ledger;
using Radirec.Core.Model;

namespace Radirec.Tests;

public sealed class SqliteLedgerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "radirec-ledger-" + Guid.NewGuid().ToString("N"));

    public SqliteLedgerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "ledger.db");

    private static LedgerEntry Entry(IdentityKey key, string source = "/in/a.m4a") => new()
    {
        IdentityKey = key.Value,
        Station = key.CanonicalStation,
        Program = key.CanonicalProgram,
        BroadcastDate = key.BroadcastDate,
        SourcePath = source,
        OutputPath = "/out/a.m4a",
        RuleVersion = NormalizationRuleVersion.Current,
        UsedFallback = false,
        ImportedAtUtc = new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero),
    };

    private static IdentityKey Key(string program = "番組") =>
        new("J-WAVE", program, new DateOnly(2026, 6, 12));

    [Fact]
    public void Record_ThenContains_IsTrueForKeyAndSource()
    {
        using var ledger = new SqliteLedger(DbPath);
        var key = Key();

        ledger.Record(Entry(key, source: "/in/x.m4a"));

        Assert.True(ledger.Contains(key));
        Assert.True(ledger.ContainsSource("/in/x.m4a"));
        Assert.False(ledger.Contains(Key("別番組")));
        Assert.False(ledger.ContainsSource("/in/other.m4a"));
    }

    [Fact]
    public void Record_DuplicateKey_IsIgnored()
    {
        using var ledger = new SqliteLedger(DbPath);
        var key = Key();

        ledger.Record(Entry(key));
        ledger.Record(Entry(key)); // 例外なく無視される

        Assert.True(ledger.Contains(key));
    }

    [Fact]
    public void Persists_AcrossReopen()
    {
        var key = Key();
        using (var ledger = new SqliteLedger(DbPath))
        {
            ledger.Record(Entry(key));
        }

        using var reopened = new SqliteLedger(DbPath);
        Assert.True(reopened.Contains(key));
    }

    [Fact]
    public void Creates_MissingParentDirectory()
    {
        // 親ディレクトリが存在しないパスでも初回作成できる(サンプル設定の C:\ProgramData\radirec\... 相当)。
        var nested = Path.Combine(_dir, "sub", "dir", "ledger.db");

        using var ledger = new SqliteLedger(nested);
        ledger.Record(Entry(Key()));

        Assert.True(File.Exists(nested));
        Assert.True(ledger.Contains(Key()));
    }
}
