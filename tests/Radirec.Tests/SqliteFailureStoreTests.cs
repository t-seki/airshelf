using Microsoft.Data.Sqlite;
using Radirec.Core.Ingestion;
using Radirec.Core.Ledger;

namespace Radirec.Tests;

/// <summary>
/// 取込失敗 de-dup ストア(<see cref="SqliteLedger"/> の <see cref="IIngestFailureStore"/> 面。ADR-0011)。
/// SQLite は WSL でも動くので、要の「新規のみ / 内容変化で再通知 / 成功で解除 / **再起動を跨いで抑制継続**」を
/// ここで固定する(トースト提示のフェーズだけが Windows 実機限定)。要対応(#172)に出す理由・時刻と、
/// 理由・時刻を持たない旧い台帳の移行もここで確かめる。
/// </summary>
public sealed class SqliteFailureStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "radirec-failstore-" + Guid.NewGuid().ToString("N"));

    public SqliteFailureStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "ledger.db");

    private static readonly FileFingerprint Fp = new(1024, 638_000_000_000_000_000);

    private static readonly DateTimeOffset At = new(2026, 9, 25, 12, 34, 0, TimeSpan.Zero);

    private static void Record(SqliteLedger store, string path, FileFingerprint fingerprint, string reason = "理由")
        => store.Record(path, fingerprint, reason, At);

    [Fact]
    public void 記録前は未知_記録後は既知()
    {
        using var store = new SqliteLedger(DbPath);

        Assert.False(store.IsKnown("/in/a.m4a", Fp));
        Record(store, "/in/a.m4a", Fp);
        Assert.True(store.IsKnown("/in/a.m4a", Fp));
    }

    [Fact]
    public void 内容が変わると別フィンガープリントで未知に戻る()
    {
        using var store = new SqliteLedger(DbPath);
        Record(store, "/in/a.m4a", Fp);

        var changed = Fp with { SizeBytes = Fp.SizeBytes + 1 };
        Assert.False(store.IsKnown("/in/a.m4a", changed)); // 差し替え → 再通知対象
        Assert.True(store.IsKnown("/in/a.m4a", Fp));
    }

    [Fact]
    public void 成功で消すと未知に戻る()
    {
        using var store = new SqliteLedger(DbPath);
        Record(store, "/in/a.m4a", Fp);

        store.Clear("/in/a.m4a");

        Assert.False(store.IsKnown("/in/a.m4a", Fp));
        Assert.Empty(store.List());
    }

    [Fact]
    public void 記録は再起動をまたいで永続する_再オープン()
    {
        using (var store = new SqliteLedger(DbPath))
            Record(store, "/in/a.m4a", Fp);

        // 別インスタンス = 常駐の再起動相当。抑制が継続し、再起動で全再通知しないこと。
        using var reopened = new SqliteLedger(DbPath);
        Assert.True(reopened.IsKnown("/in/a.m4a", Fp));
    }

    [Fact]
    public void 同じパスの再Recordはフィンガープリントと理由と時刻を更新する()
    {
        using var store = new SqliteLedger(DbPath);
        Record(store, "/in/a.m4a", Fp, reason: "前の理由");

        var changed = Fp with { LastWriteUtcTicks = Fp.LastWriteUtcTicks + 100 };
        var later = At.AddHours(1);
        store.Record("/in/a.m4a", changed, "後の理由", later);

        Assert.True(store.IsKnown("/in/a.m4a", changed));
        Assert.False(store.IsKnown("/in/a.m4a", Fp)); // 旧 fingerprint は残さない(PK=パス)
        var record = Assert.Single(store.List());
        Assert.Equal("後の理由", record.Reason);
        Assert.Equal(later, record.FailedAtUtc);
    }

    // ---- 要対応に出す理由と時刻(#172)----

    [Fact]
    public void 一覧は理由と記録した時刻を返す()
    {
        using var store = new SqliteLedger(DbPath);
        Record(store, "/in/a.m4a", Fp, reason: "a.m4a の名前を解析できません");

        var record = Assert.Single(store.List());

        Assert.Equal("/in/a.m4a", record.SourcePath);
        Assert.Equal("a.m4a の名前を解析できません", record.Reason);
        Assert.Equal(At, record.FailedAtUtc);
    }

    // #172 より前の台帳の failures(理由・時刻の列が無い)を作り、1 行入れる。
    private void CreateLegacyFailuresDb(string sourcePath)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE failures (source_path TEXT NOT NULL PRIMARY KEY, size_bytes INTEGER NOT NULL, " +
            "last_write INTEGER NOT NULL);" +
            "INSERT INTO failures VALUES ($p, 1024, 638000000000000000);";
        cmd.Parameters.AddWithValue("$p", sourcePath);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void 旧い台帳を移行でき_移行前の行は理由も時刻もnullで読める()
    {
        CreateLegacyFailuresDb("/in/old.m4a");

        using var store = new SqliteLedger(DbPath);

        var old = Assert.Single(store.List());
        Assert.Equal("/in/old.m4a", old.SourcePath);
        Assert.Null(old.Reason);
        Assert.Null(old.FailedAtUtc);
        Assert.True(store.IsKnown("/in/old.m4a", Fp)); // de-dup の記録は失わない

        // 移行後は理由と時刻を書ける。
        Record(store, "/in/new.m4a", Fp, reason: "新しい失敗");
        Assert.Equal("新しい失敗", store.List().Single(r => r.SourcePath == "/in/new.m4a").Reason);
    }

    [Fact]
    public void 移行は冪等_開き直しても行を失わない()
    {
        CreateLegacyFailuresDb("/in/old.m4a");
        using (var first = new SqliteLedger(DbPath))
            Record(first, "/in/new.m4a", Fp);

        using var reopened = new SqliteLedger(DbPath);

        Assert.Equal(2, reopened.List().Count);
    }

    [Fact]
    public void 読み取り専用で未移行の台帳を開いても_一覧は理由なしで読める()
    {
        // 管理画面の要対応は、読み取り専用の接続で台帳を読む(取り込みワーカーが移行する前の台帳もありうる)。
        CreateLegacyFailuresDb("/in/old.m4a");

        using var readOnly = new SqliteLedger(DbPath, readOnly: true);

        var old = Assert.Single(readOnly.List());
        Assert.Null(old.Reason);
        Assert.Null(old.FailedAtUtc);
    }

    [Fact]
    public void 移行が途中で止まった台帳も_読み取り専用で理由なしで読め_書き込みで開けば移行を終える()
    {
        // 列の追加は 2 本の ALTER で、1 本ずつ確定する。reason だけ足されて failed_at が無い台帳を作る。
        CreateLegacyFailuresDb("/in/old.m4a");
        using (var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE failures ADD COLUMN reason TEXT NULL;";
            cmd.ExecuteNonQuery();
        }

        using (var readOnly = new SqliteLedger(DbPath, readOnly: true))
            Assert.Null(Assert.Single(readOnly.List()).Reason);

        using var store = new SqliteLedger(DbPath);
        Record(store, "/in/new.m4a", Fp, reason: "新しい失敗");
        Assert.Equal(At, store.List().Single(r => r.SourcePath == "/in/new.m4a").FailedAtUtc);
    }

    [Fact]
    public void 読み取り専用でfailuresの無い台帳を開くと_一覧は空()
    {
        using (var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE other (x INTEGER);";
            cmd.ExecuteNonQuery();
        }

        using var readOnly = new SqliteLedger(DbPath, readOnly: true);

        Assert.Empty(readOnly.List());
    }
}
