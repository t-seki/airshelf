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
        // 旧スキーマ seed 用の生 SqliteConnection も Pooling=False にしてあり(SqliteLedger 本体と同じ)、
        // プールに残って ledger.db のハンドルを握る接続は無い。そのため ClearAllPools は呼ばなくてよい。
        // プールする接続を作ると、並列に走るほかのテストが呼ぶ ClearAllPools が、使用中の
        // 接続のハンドルを破棄しうる(ExecuteNonQuery で ObjectDisposedException。#175)。
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
    public void ListAll_empty_when_no_records()
    {
        using var ledger = new SqliteLedger(DbPath);
        Assert.Empty(ledger.ListAll());
    }

    [Fact]
    public void ListAll_returns_entries_newest_first_and_round_trips_fields()
    {
        using var ledger = new SqliteLedger(DbPath);

        var older = Entry(Key("番組A"), source: @"I:\radio\a.m4a") with
        {
            ImportedAtUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var newer = Entry(Key("番組B"), source: @"I:\radio\b.m4a", time: new TimeOnly(8, 30), part: 2) with
        {
            ImportedAtUtc = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
            OutputPath = @"\\nas\share\b.m4a",
            UsedFallback = true,
        };
        ledger.Record(older);
        ledger.Record(newer);

        var all = ledger.ListAll();

        Assert.Equal(2, all.Count);
        // 取込日時の降順(新しい順)。
        Assert.Equal("番組B", all[0].Program);
        Assert.Equal("番組A", all[1].Program);

        // 新しい方のフィールドが round-trip する。
        var b = all[0];
        Assert.Equal("J-WAVE", b.Station);
        Assert.Equal(new DateOnly(2026, 6, 12), b.BroadcastDate);
        Assert.Equal(new TimeOnly(8, 30), b.BroadcastTime);
        Assert.Equal(2, b.PartIndex);
        Assert.Equal(@"I:\radio\b.m4a", b.SourcePath);
        Assert.Equal(@"\\nas\share\b.m4a", b.OutputPath);
        Assert.True(b.UsedFallback);
        Assert.Equal(newer.ImportedAtUtc, b.ImportedAtUtc);

        // 時刻なしは null に戻る(TimeKey "" の逆)。
        Assert.Null(all[1].BroadcastTime);
    }

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

    // ---- Replace(統合設定の再適用。ADR-0016)----

    [Fact]
    public void Replace_ChangesPrimaryKey_AndKeepsOtherColumns()
    {
        using var ledger = new SqliteLedger(DbPath);
        var oldKey = Key("番組(1)");
        var newKey = Key("番組");
        var time = new TimeOnly(8, 30);
        var current = Entry(oldKey, source: "/in/a.m4a", time: time) with
        {
            Fingerprint = new FileFingerprint(1234, 5678),
            UsedFallback = true,
        };
        ledger.Record(current);

        var updated = current with
        {
            IdentityKey = newKey.Value,
            Program = "番組",
            PartIndex = 1,
            OutputPath = "/out/番組/番組 2026-06-12-1.m4a",
        };
        Assert.True(ledger.Replace(current, updated));

        Assert.False(ledger.Contains(oldKey, time, 0));
        Assert.True(ledger.Contains(newKey, time, 1));
        var row = Assert.Single(ledger.ListAll());
        Assert.Equal("番組", row.Program);
        Assert.Equal(1, row.PartIndex);
        Assert.Equal("/out/番組/番組 2026-06-12-1.m4a", row.OutputPath);
        // 書き換えない列は元のまま(取込元・取込日時・内容フィンガープリント・フォールバック)。
        Assert.Equal("/in/a.m4a", row.SourcePath);
        Assert.Equal(current.ImportedAtUtc, row.ImportedAtUtc);
        Assert.Equal(new FileFingerprint(1234, 5678), row.Fingerprint);
        Assert.True(row.UsedFallback);
        Assert.True(ledger.ContainsContent(new FileFingerprint(1234, 5678), row.BroadcastDate, time));
    }

    [Fact]
    public void Replace_SamePrimaryKey_UpdatesInPlace()
    {
        // 正規名の空白だけが変わる(LOGISTEEDRADIONOMICS → LOGISTEED RADIONOMICS)と同一性キーは変わらない。
        // 旧行を消してから足すので、自分自身とは衝突しない。
        using var ledger = new SqliteLedger(DbPath);
        var before = Key("LOGISTEEDRADIONOMICS");
        var after = Key("LOGISTEED RADIONOMICS");
        Assert.Equal(before.Value, after.Value);
        var current = Entry(before);
        ledger.Record(current);

        Assert.True(ledger.Replace(current, current with { Program = "LOGISTEED RADIONOMICS" }));

        Assert.Equal("LOGISTEED RADIONOMICS", Assert.Single(ledger.ListAll()).Program);
    }

    [Fact]
    public void Replace_WhenNewKeyIsTakenByAnotherRow_ReturnsFalse_AndChangesNothing()
    {
        using var ledger = new SqliteLedger(DbPath);
        var current = Entry(Key("番組(1)"), source: "/in/a.m4a");
        var other = Entry(Key("番組"), source: "/in/b.m4a", part: 1);
        ledger.Record(current);
        ledger.Record(other);

        var updated = current with { IdentityKey = Key("番組").Value, Program = "番組", PartIndex = 1 };
        Assert.False(ledger.Replace(current, updated));

        // ロールバックされ、旧行は消えていない。
        Assert.True(ledger.Contains(Key("番組(1)"), null, 0));
        Assert.Equal(2, ledger.ListAll().Count);
        Assert.Equal("/in/b.m4a", ledger.ListAll().Single(e => e.Program == "番組").SourcePath);
    }

    [Fact]
    public void Replace_WhenCurrentRowIsMissing_ReturnsFalse_AndAddsNothing()
    {
        using var ledger = new SqliteLedger(DbPath);
        var missing = Entry(Key("番組(1)"));

        Assert.False(ledger.Replace(missing, missing with { IdentityKey = Key("番組").Value, Program = "番組" }));

        Assert.Empty(ledger.ListAll());
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
        // 親ディレクトリが存在しないパスでも初回作成できる(初回起動時のデータルート生成に相当)。
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

    [Fact]
    public void TimelessQuery_DoesNotMatchTimeBearingRow_NoWildcard()
    {
        // ADR-0004 でワイルドカードを撤去したことをロックする(#11 の再発防止)。
        // 旧実装では時刻なし照会が時刻ありの行に一致して誤スキップしていた。
        using var ledger = new SqliteLedger(DbPath);
        var key = Key();
        ledger.Record(Entry(key, source: "/in/a.m4a", time: new TimeOnly(8, 30, 0), part: 0));

        Assert.False(ledger.Contains(key, null, 0));               // 時刻なし照会は時刻あり行に一致しない
        Assert.False(ledger.Contains(key, new TimeOnly(8, 30), 1)); // part が違えば一致しない
        Assert.True(ledger.Contains(key, new TimeOnly(8, 30), 0));  // 完全一致のみ取込済み
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

    // ---- content ゲート(フィンガープリント + 放送日時。#79 / ADR-0014)----

    private static readonly FileFingerprint Fp = new(5000, 638_000_000_000_000_000L);

    [Fact]
    public void ContainsContent_TrueForSameFingerprintAndSlot_PathIndependent()
    {
        using var ledger = new SqliteLedger(DbPath);
        // 別 identity(番組フォルダ違い)・別ソースパスでも、内容 + 放送日時が一致すれば取込済み。
        ledger.Record(Entry(Key("ふらっと(1)"), source: @"I:\a\x.m4a", time: new TimeOnly(8, 30)) with
        {
            Fingerprint = Fp,
        });

        Assert.True(ledger.ContainsContent(Fp, new DateOnly(2026, 6, 12), new TimeOnly(8, 30)));
        Assert.False(ledger.Contains(Key("ふらっと(2)"), new TimeOnly(8, 30), 2)); // identity/part は別物
    }

    [Fact]
    public void ContainsContent_FalseWhenContentOrSlotDiffers()
    {
        using var ledger = new SqliteLedger(DbPath);
        ledger.Record(Entry(Key(), time: new TimeOnly(8, 30)) with { Fingerprint = Fp });

        Assert.False(ledger.ContainsContent(Fp with { SizeBytes = 6000 }, new DateOnly(2026, 6, 12), new TimeOnly(8, 30)));
        Assert.False(ledger.ContainsContent(Fp, new DateOnly(2026, 6, 12), new TimeOnly(10, 0))); // 別時刻
        Assert.False(ledger.ContainsContent(Fp, new DateOnly(2026, 6, 13), new TimeOnly(8, 30))); // 別日
    }

    [Fact]
    public void ContainsContent_RoundTripsThroughListAll()
    {
        using var ledger = new SqliteLedger(DbPath);
        ledger.Record(Entry(Key(), time: new TimeOnly(8, 30)) with { Fingerprint = Fp });

        Assert.Equal(Fp, ledger.ListAll().Single().Fingerprint);
    }

    [Fact]
    public void Migrates_BroadcastTimeSchema_AddsContentColumns_AndContentGateWorks()
    {
        // 本番の現行スキーマ(broadcast_time あり・part_index / content 列なし)を開くと content 列が
        // 後付けされ、新規記録は content ゲートで判定できる。移送された旧行(内容不明 = 0,0)は実ファイル
        // (サイズ > 0)の照会に一致しない(誤スキップしない)。
        CreateBroadcastTimeSchemaDb(Key(), source: "/in/prod.m4a", time: "083000");

        using var ledger = new SqliteLedger(DbPath);

        ledger.Record(Entry(Key("別番組"), time: new TimeOnly(1, 0)) with { Fingerprint = Fp });
        Assert.True(ledger.ContainsContent(Fp, new DateOnly(2026, 6, 12), new TimeOnly(1, 0)));
        // 旧行(0,0)は実ファイルのフィンガープリントに一致しない。
        Assert.False(ledger.ContainsContent(new FileFingerprint(4096, 123), new DateOnly(2026, 6, 12), new TimeOnly(8, 30, 0)));
    }

    [Fact]
    public void Migrates_PartIndexSchema_AddsContentColumns_ViaAlter_PreservesRows()
    {
        // 本番で実際に通る経路: M4 スキーマ(part_index あり・content 列なし)を書き込みで開くと、
        // EnsureContentColumns が ALTER ADD COLUMN で列を後付けする(rename→copy はしない)。既存行は保持され、
        // 新規記録は content ゲートで判定できる。移送された旧行(0,0)は実ファイルに一致しない。
        var key = Key();
        CreatePartIndexSchemaDb(key, source: "/in/m4.m4a", time: "083000");

        using var ledger = new SqliteLedger(DbPath);

        Assert.True(ledger.Contains(key, new TimeOnly(8, 30, 0), 0)); // 既存行は保持
        Assert.True(ledger.ContainsSource("/in/m4.m4a"));

        ledger.Record(Entry(Key("別番組"), time: new TimeOnly(1, 0)) with { Fingerprint = Fp });
        Assert.True(ledger.ContainsContent(Fp, new DateOnly(2026, 6, 12), new TimeOnly(1, 0)));
        Assert.False(ledger.ContainsContent(new FileFingerprint(4096, 123), new DateOnly(2026, 6, 12), new TimeOnly(8, 30, 0)));
    }

    [Fact]
    public void ReadOnly_WithoutContentColumns_ContainsContent_IsFalse_NoThrow()
    {
        // content 列を持たない現行スキーマを dry-run(読み取り専用)で開いても例外にならず未取込扱い。
        CreateBroadcastTimeSchemaDb(Key(), source: "/in/prod.m4a", time: "083000");

        using var ledger = new SqliteLedger(DbPath, readOnly: true);

        Assert.False(ledger.ContainsContent(new FileFingerprint(4096, 123), new DateOnly(2026, 6, 12), new TimeOnly(8, 30, 0)));
    }

    [Fact]
    public void ReadOnly_PartIndexSchemaWithoutContentColumns_ListAll_ReturnsRows_NoThrow()
    {
        // M4 スキーマ(broadcast_time + part_index あり・content 列なし)を取込履歴ビューが読み取り専用で
        // 開くと _usable=true(part_index あり)。content 列を無条件 SELECT すると「no such column: size_bytes」で
        // 履歴ビューが落ちる回帰の固定。read-only は移行しないので列は無いまま。
        CreatePartIndexSchemaDb(Key(), source: "/in/prod.m4a", time: "083000");

        using var ledger = new SqliteLedger(DbPath, readOnly: true);

        var row = Assert.Single(ledger.ListAll());
        Assert.Equal("/in/prod.m4a", row.SourcePath);
        Assert.Equal(default, row.Fingerprint); // content 列が無いので既定 (0,0)
    }

    // 最古スキーマ(broadcast_time / part_index 列なし)の台帳に 1 行入れて作る
    private void CreateOldestSchemaDb(IdentityKey key, string source)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False");
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

    // M4 スキーマ(broadcast_time + part_index あり・content 列なし)に 1 行入れて作る。
    // content フィンガープリント列を後付けする移行(#79 / ADR-0014)前の本番台帳を模す。
    private void CreatePartIndexSchemaDb(IdentityKey key, string source, string time)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE imports (identity_key TEXT NOT NULL, broadcast_time TEXT NOT NULL DEFAULT '', " +
            "part_index INTEGER NOT NULL DEFAULT 0, station TEXT, program TEXT, broadcast_date TEXT, " +
            "source_path TEXT, output_path TEXT, rule_version TEXT, used_fallback INTEGER, imported_at TEXT, " +
            "PRIMARY KEY (identity_key, broadcast_time, part_index));" +
            "INSERT INTO imports VALUES ($id,$t,0,'局','番組','2026-06-12',$src,'/out/a.m4a','m4-1',0,'2026-06-12T00:00:00Z');";
        cmd.Parameters.AddWithValue("$id", key.Value);
        cmd.Parameters.AddWithValue("$t", time);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.ExecuteNonQuery();
    }

    // 現行スキーマ(broadcast_time あり・part_index なし = 本番台帳)に 1 行入れて作る
    private void CreateBroadcastTimeSchemaDb(IdentityKey key, string source, string time)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath};Pooling=False");
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

    [Fact]
    public void ReadOnly_OnBroadcastTimeSchema_DoesNotThrow_AndReportsNotContained()
    {
        // 本番の現行スキーマ(broadcast_time あり・part_index なし)を dry-run で読み取り専用オープン。
        // Contains は part_index 列を参照するため、_usable=false で例外を出さず未取込扱いにする
        // (書き込み実行時に自動マイグレーションされるまでの一時的挙動)。
        CreateBroadcastTimeSchemaDb(Key(), source: "/in/prod.m4a", time: "083000");

        using var ledger = new SqliteLedger(DbPath, readOnly: true);

        Assert.False(ledger.Contains(Key(), new TimeOnly(8, 30, 0), 0));
        Assert.False(ledger.ContainsSource("/in/prod.m4a"));
    }
}
