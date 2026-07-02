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

    private static LedgerEntry Entry(IdentityKey key, string source = "/in/a.m4a", TimeOnly? time = null) => new()
    {
        IdentityKey = key.Value,
        Station = key.CanonicalStation,
        Program = key.CanonicalProgram,
        BroadcastDate = key.BroadcastDate,
        BroadcastTime = time,
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

        Assert.True(ledger.Contains(key, null));
        Assert.True(ledger.ContainsSource("/in/x.m4a"));
        Assert.False(ledger.Contains(Key("別番組"), null));
        Assert.False(ledger.ContainsSource("/in/other.m4a"));
    }

    [Fact]
    public void Record_DuplicateKey_IsIgnored()
    {
        using var ledger = new SqliteLedger(DbPath);
        var key = Key();

        ledger.Record(Entry(key));
        ledger.Record(Entry(key)); // 例外なく無視される

        Assert.True(ledger.Contains(key, null));
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
        Assert.True(reopened.Contains(key, null));
    }

    [Fact]
    public void Creates_MissingParentDirectory()
    {
        // 親ディレクトリが存在しないパスでも初回作成できる(サンプル設定の C:\ProgramData\radirec\... 相当)。
        var nested = Path.Combine(_dir, "sub", "dir", "ledger.db");

        using var ledger = new SqliteLedger(nested);
        ledger.Record(Entry(Key()));

        Assert.True(File.Exists(nested));
        Assert.True(ledger.Contains(Key(), null));
    }

    // ---- 同日 tie-break(ADR-0002)----

    [Fact]
    public void SameKey_DifferentTime_AreDistinct()
    {
        using var ledger = new SqliteLedger(DbPath);
        var key = Key();
        var t0830 = new TimeOnly(8, 30, 0);
        var t1000 = new TimeOnly(10, 0, 0);

        ledger.Record(Entry(key, source: "/in/p1.m4a", time: t0830));

        Assert.True(ledger.Contains(key, t0830));    // 同時刻は取込済み
        Assert.False(ledger.Contains(key, t1000));   // 別時刻はまだ未取込 → 取り込める

        ledger.Record(Entry(key, source: "/in/p2.m4a", time: t1000));
        Assert.True(ledger.Contains(key, t1000));    // 2件目も別レコードとして記録される
    }

    [Fact]
    public void MissingTime_IsWildcard_ForCrossSource()
    {
        using var ledger = new SqliteLedger(DbPath);
        var key = Key();

        // らじれこ(時刻あり)を記録
        ledger.Record(Entry(key, time: new TimeOnly(8, 30, 0)));

        // 時刻なし(radikool 相当)の照会は同日どの時刻とも一致(重複排除)
        Assert.True(ledger.Contains(key, null));

        // 逆向き: 時刻なしを記録済みなら、時刻ありの照会も一致
        var key2 = Key("別番組");
        ledger.Record(Entry(key2, source: "/in/rk.m4a", time: null));
        Assert.True(ledger.Contains(key2, new TimeOnly(1, 23, 45)));
    }
}
