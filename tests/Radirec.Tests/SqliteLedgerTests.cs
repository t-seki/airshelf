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

    private static LedgerEntry Entry(
        IdentityKey key, string source = "/in/a.m4a", TimeOnly? time = null, int part = 0) => new()
    {
        IdentityKey = key.Value,
        Station = key.CanonicalStation,
        Program = key.CanonicalProgram,
        BroadcastDate = key.BroadcastDate,
        BroadcastTime = time,
        PartIndex = part,
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

        Assert.True(ledger.Contains(key, null, 0));
        Assert.True(ledger.ContainsSource("/in/x.m4a"));
        Assert.False(ledger.Contains(Key("別番組"), null, 0));
        Assert.False(ledger.ContainsSource("/in/other.m4a"));
    }

    [Fact]
    public void Record_DuplicateKey_IsIgnored()
    {
        using var ledger = new SqliteLedger(DbPath);
        var key = Key();

        ledger.Record(Entry(key));
        ledger.Record(Entry(key)); // 例外なく無視される

        Assert.True(ledger.Contains(key, null, 0));
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
        Assert.True(reopened.Contains(key, null, 0));
    }

    [Fact]
    public void Creates_MissingParentDirectory()
    {
        // 親ディレクトリが存在しないパスでも初回作成できる(サンプル設定の C:\ProgramData\radirec\... 相当)。
        var nested = Path.Combine(_dir, "sub", "dir", "ledger.db");

        using var ledger = new SqliteLedger(nested);
        ledger.Record(Entry(Key()));

        Assert.True(File.Exists(nested));
        Assert.True(ledger.Contains(Key(), null, 0));
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

        Assert.True(ledger.Contains(key, t0830, 0));    // 同時刻は取込済み
        Assert.False(ledger.Contains(key, t1000, 0));   // 別時刻はまだ未取込 → 取り込める

        ledger.Record(Entry(key, source: "/in/p2.m4a", time: t1000));
        Assert.True(ledger.Contains(key, t1000, 0));    // 2件目も別レコードとして記録される
    }

    // ---- 時刻なしソースの同日重複を part で区別(ADR-0004。#11 の回帰防止)----

    [Fact]
    public void SameKey_NoTime_DifferentPart_AreDistinct_BothRecorded()
    {
        // radikool の ANN0(ZERO) 2020-02-23 bare + (2) 相当。時刻なしで同日2録音。
        // 旧ワイルドカードでは 2 件目が誤スキップされ 1 行しか残らなかった(#11)。
        using var ledger = new SqliteLedger(DbPath);
        var key = Key();

        ledger.Record(Entry(key, source: "/in/bare.mp3", time: null, part: 0));

        Assert.True(ledger.Contains(key, null, 0));   // bare は取込済み
        Assert.False(ledger.Contains(key, null, 2));  // (2) はまだ未取込 → 取り込める(誤スキップしない)

        ledger.Record(Entry(key, source: "/in/2.mp3", time: null, part: 2));

        // 両方が別レコードとして残る。
        Assert.True(ledger.Contains(key, null, 0));
        Assert.True(ledger.Contains(key, null, 2));
        Assert.True(ledger.ContainsSource("/in/bare.mp3"));
        Assert.True(ledger.ContainsSource("/in/2.mp3"));
    }

    // 最古スキーマ(broadcast_time / part_index 列なし)の台帳に 1 行入れて作る
    private void CreateOldestSchemaDb(IdentityKey key, string source)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE imports (identity_key TEXT PRIMARY KEY, station TEXT, program TEXT, " +
            "broadcast_date TEXT, source_path TEXT, output_path TEXT, rule_version TEXT, " +
            "used_fallback INTEGER, imported_at TEXT);" +
            "INSERT INTO imports VALUES ($id,'局','番組','2026-06-12',$src,'/out/a.m4a','m1-1',0,'2026-06-12T00:00:00Z');";
        cmd.Parameters.AddWithValue("$id", key.Value);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.ExecuteNonQuery();
    }

    // 現行スキーマ(broadcast_time あり・part_index なし = 本番台帳)に 1 行入れて作る
    private void CreateBroadcastTimeSchemaDb(IdentityKey key, string source, string time)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE imports (identity_key TEXT NOT NULL, broadcast_time TEXT NOT NULL DEFAULT '', " +
            "station TEXT, program TEXT, broadcast_date TEXT, source_path TEXT, output_path TEXT, " +
            "rule_version TEXT, used_fallback INTEGER, imported_at TEXT, " +
            "PRIMARY KEY (identity_key, broadcast_time));" +
            "INSERT INTO imports VALUES ($id,$t,'局','番組','2026-06-12',$src,'/out/a.m4a','m3-1',0,'2026-06-12T00:00:00Z');";
        cmd.Parameters.AddWithValue("$id", key.Value);
        cmd.Parameters.AddWithValue("$t", time);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Migrates_OldestSchema_PreservesExistingRows()
    {
        var key = Key();
        CreateOldestSchemaDb(key, source: "/in/legacy.m4a");

        using var ledger = new SqliteLedger(DbPath);

        // 旧行は broadcast_time='' / part_index=0 として移送される。厳密一致で取込済み判定。
        Assert.True(ledger.Contains(key, null, 0));
        Assert.True(ledger.ContainsSource("/in/legacy.m4a")); // 再取込は source_path で防ぐ
        // 新スキーマとして書き込みも可能
        ledger.Record(Entry(Key("別番組"), time: new TimeOnly(1, 0, 0)));
        Assert.True(ledger.Contains(Key("別番組"), new TimeOnly(1, 0, 0), 0));
    }

    [Fact]
    public void Migrates_BroadcastTimeSchema_AddsPartIndex_PreservesRows()
    {
        // 本番台帳(M3 時点 = broadcast_time あり・part_index なし)を part_index 追加へ移送する。
        var key = Key();
        CreateBroadcastTimeSchemaDb(key, source: "/in/prod.m4a", time: "083000");

        using var ledger = new SqliteLedger(DbPath);

        // 既存の時刻は保持され、part_index=0 で移送される。
        Assert.True(ledger.Contains(key, new TimeOnly(8, 30, 0), 0));
        Assert.True(ledger.ContainsSource("/in/prod.m4a"));
        // 移送後は part 付きの新規記録が可能。
        ledger.Record(Entry(key, source: "/in/prod2.mp3", time: null, part: 2));
        Assert.True(ledger.Contains(key, null, 2));
    }

    [Fact]
    public void ReadOnly_OnOldSchema_DoesNotThrow_AndReportsNotContained()
    {
        CreateOldestSchemaDb(Key(), source: "/in/legacy.m4a");

        // 読み取り専用(dry-run 経路)で旧スキーマを開いても例外にならず、未取込扱いになる。
        using var ledger = new SqliteLedger(DbPath, readOnly: true);

        Assert.False(ledger.Contains(Key(), new TimeOnly(8, 30, 0), 0));
        Assert.False(ledger.ContainsSource("/in/legacy.m4a"));
    }
}
