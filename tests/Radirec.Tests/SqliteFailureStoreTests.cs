using Radirec.Core.Ingestion;
using Radirec.Core.Ledger;

namespace Radirec.Tests;

/// <summary>
/// 取込失敗 de-dup ストア(<see cref="SqliteLedger"/> の <see cref="IIngestFailureStore"/> 面。ADR-0011)。
/// SQLite は WSL でも動くので、要の「新規のみ / 内容変化で再通知 / 成功で解除 / **再起動を跨いで抑制継続**」を
/// ここで固定する(EventLog のフェーズだけが Windows 実機限定)。
/// </summary>
public sealed class SqliteFailureStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "radirec-failstore-" + Guid.NewGuid().ToString("N"));

    public SqliteFailureStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "ledger.db");

    private static readonly FileFingerprint Fp = new(1024, 638_000_000_000_000_000);

    [Fact]
    public void 記録前は未知_記録後は既知()
    {
        using var store = new SqliteLedger(DbPath);

        Assert.False(store.IsKnown("/in/a.m4a", Fp));
        store.Record("/in/a.m4a", Fp);
        Assert.True(store.IsKnown("/in/a.m4a", Fp));
    }

    [Fact]
    public void 内容が変わると別フィンガープリントで未知に戻る()
    {
        using var store = new SqliteLedger(DbPath);
        store.Record("/in/a.m4a", Fp);

        var changed = Fp with { SizeBytes = Fp.SizeBytes + 1 };
        Assert.False(store.IsKnown("/in/a.m4a", changed)); // 差し替え → 再通知対象
        Assert.True(store.IsKnown("/in/a.m4a", Fp));
    }

    [Fact]
    public void 成功で消すと未知に戻る()
    {
        using var store = new SqliteLedger(DbPath);
        store.Record("/in/a.m4a", Fp);

        store.Clear("/in/a.m4a");

        Assert.False(store.IsKnown("/in/a.m4a", Fp));
    }

    [Fact]
    public void 記録は再起動をまたいで永続する_再オープン()
    {
        using (var store = new SqliteLedger(DbPath))
            store.Record("/in/a.m4a", Fp);

        // 別インスタンス = サービス再起動相当。抑制が継続し、再起動で全再通知しないこと。
        using var reopened = new SqliteLedger(DbPath);
        Assert.True(reopened.IsKnown("/in/a.m4a", Fp));
    }

    [Fact]
    public void 同じパスの再Recordはフィンガープリントを更新する()
    {
        using var store = new SqliteLedger(DbPath);
        store.Record("/in/a.m4a", Fp);

        var changed = Fp with { LastWriteUtcTicks = Fp.LastWriteUtcTicks + 100 };
        store.Record("/in/a.m4a", changed);

        Assert.True(store.IsKnown("/in/a.m4a", changed));
        Assert.False(store.IsKnown("/in/a.m4a", Fp)); // 旧 fingerprint は残さない(PK=パス)
    }
}
