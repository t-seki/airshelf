using System.Globalization;
using Microsoft.Data.Sqlite;
using Radirec.Core.Model;

namespace Radirec.Core.Ledger;

/// <summary>
/// SQLite(WAL)によるローカルディスク台帳。SMB(出力先)には置かない前提。取込済み記録(import-once)に
/// 加えて、取込失敗の de-dup ストア(<see cref="IIngestFailureStore"/>。ADR-0011)を同一 DB の別テーブルで
/// 相乗りさせる(Windows の SQLite ファイルロックを増やさないため別ファイルにしない)。保留のストア
/// (<see cref="IHeldRecordingStore"/>。ADR-0021)も同じく別テーブルで相乗りさせる。
/// </summary>
public sealed class SqliteLedger : ILedger, IIngestFailureStore, IHeldRecordingStore, IDisposable
{
    private const string DateFormat = "yyyy-MM-dd";

    // 取込失敗の de-dup テーブル。鍵 = ソースパス。フィンガープリント(サイズ + 最終更新 ticks)が
    // 一致するときだけ「通知済み」とみなす(内容変化で再通知)。理由(reason)と記録した時刻(failed_at。ISO 8601)は
    // 要対応に出すため(#172)。後から足した列なので NULL を許す(移行前の行は理由も時刻も持たない)。
    private const string CreateFailuresTableSql =
        """
        CREATE TABLE IF NOT EXISTS failures (
            source_path TEXT NOT NULL PRIMARY KEY,
            size_bytes  INTEGER NOT NULL,
            last_write  INTEGER NOT NULL,
            reason      TEXT NULL,
            failed_at   TEXT NULL
        );
        """;

    // 保留のストア(ADR-0021)。鍵 = ソースパス。要対応に (局, フォルダ, ファイル名の番組名) の組でまとめて出すため、
    // それぞれを列に持つ(表示のたびにファイル名を解析し直さない)。held_at は初めて保留した時刻(ISO 8601)。
    private const string CreateHeldTableSql =
        """
        CREATE TABLE IF NOT EXISTS held (
            source_path      TEXT NOT NULL PRIMARY KEY,
            station          TEXT NOT NULL,
            folder           TEXT NOT NULL,
            filename_program TEXT NOT NULL,
            held_at          TEXT NOT NULL
        );
        """;

    private const string CreateTableSql =
        """
        CREATE TABLE IF NOT EXISTS imports (
            identity_key   TEXT NOT NULL,
            broadcast_time TEXT NOT NULL DEFAULT '',
            part_index     INTEGER NOT NULL DEFAULT 0,
            station        TEXT NOT NULL,
            program        TEXT NOT NULL,
            broadcast_date TEXT NOT NULL,
            source_path    TEXT NOT NULL,
            output_path    TEXT NOT NULL,
            rule_version   TEXT NOT NULL,
            used_fallback  INTEGER NOT NULL,
            imported_at    TEXT NOT NULL,
            size_bytes     INTEGER NOT NULL DEFAULT 0,
            last_write     INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (identity_key, broadcast_time, part_index)
        );
        CREATE INDEX IF NOT EXISTS ix_imports_source ON imports(source_path);
        """;

    // content ゲート(#79 / ADR-0014)の索引。size_bytes 列の存在に依存するため CreateTableSql とは
    // 分け、列を保証した後(EnsureContentColumns)に張る。
    private const string CreateContentIndexSql =
        "CREATE INDEX IF NOT EXISTS ix_imports_content ON imports(size_bytes, last_write);";

    private readonly SqliteConnection _connection;

    // 読み取り専用で開いた際、互換スキーマ(part_index 列あり)でなければ台帳を使わない。
    // 旧スキーマDBに対する dry-run が例外(no such column: part_index 等)で落ちるのを防ぐ
    // (readOnly では作り直せないため)。
    private readonly bool _usable;

    // failures テーブルが使えるか(readOnly の旧 DB でテーブル欠如時に throw させないためのガード)。
    private readonly bool _failuresUsable;

    // failures に理由・時刻の列(#172)があるか。readOnly で未移行の台帳を開いたとき、List が列を SELECT して
    // 落ちないためのガード(無ければ理由・時刻を null で返す)。
    private readonly bool _failureDetailsUsable;

    // held テーブル(ADR-0021)が使えるか。readOnly で未移行の台帳(テーブルが無い)を開いたとき、要対応が落ちないためのガード。
    private readonly bool _heldUsable;

    // content ゲート(size_bytes/last_write 列)が使えるか。readOnly で列を持たない旧/現行スキーマを
    // 開いたときに ContainsContent が例外を出さず未取込扱いにするためのガード(#79 / ADR-0014)。
    private readonly bool _contentUsable;

    /// <param name="databasePath">台帳 DB のパス(ローカルディスク)。</param>
    /// <param name="readOnly">
    /// true の場合、既存 DB を読み取り専用で開き、ファイル/ディレクトリを作成しない(dry-run 用)。
    /// 呼び出し側はファイル存在を保証すること。
    /// </param>
    public SqliteLedger(string databasePath, bool readOnly = false)
    {
        if (!readOnly)
        {
            // 親ディレクトリが無ければ作る(Microsoft.Data.Sqlite はフォルダを作らない)。
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            // 台帳は寿命いっぱい単一接続を保持するためプールは無益。無効化して Dispose 時に OS の
            // ファイルハンドルを決定的に解放する(Windows では Close 後もプールがハンドルを保持し、
            // ファイル削除/移動が「使用中」で失敗する。移行時の Copy にも好都合)。
            Pooling = false,
        }.ToString());
        _connection.Open();

        // Open 後の migration/スキーマ確認が throw したら、開いた接続を確実に破棄してからエラーを返す。
        // これを怠ると(Pooling=false で)接続ハンドルがリークする。reload は Build を繰り返し呼ぶため、
        // 失敗のたびにハンドルが漏れると長時間稼働でファイルロックの温床になる。
        try
        {
            if (!readOnly)
            {
                MigrateOrCreate();
                Exec(CreateFailuresTableSql); // 冪等。失敗 de-dup テーブルを保証する。
                EnsureFailureDetailColumns();
                Exec(CreateHeldTableSql); // 冪等。保留のストアを保証する(ADR-0021)。
                _usable = true;
                _heldUsable = true;
                _failuresUsable = true;
                _failureDetailsUsable = true;
                _contentUsable = true; // MigrateOrCreate が content 列を保証する。
            }
            else
            {
                _usable = TableExists("imports") && ColumnExists("imports", "part_index");
                _failuresUsable = TableExists("failures");
                // 列の追加は 2 本の ALTER で、1 本ずつ確定する。途中で止まった台帳(reason だけある)も未移行として扱う。
                _failureDetailsUsable = _failuresUsable
                    && ColumnExists("failures", "reason") && ColumnExists("failures", "failed_at");
                _contentUsable = _usable && ColumnExists("imports", "size_bytes");
                _heldUsable = TableExists("held");
            }
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    // 順序付き冪等マイグレーション(ADR-0002 / 0004)。ALTER では PK を変えられないため、
    // 旧行を保持したまま rename→create→INSERT SELECT→drop で新スキーマへ移送する。
    // 取込済み記録を失わず、再取込による出力重複を防ぐ。
    private void MigrateOrCreate()
    {
        Exec("PRAGMA journal_mode=WAL;");

        if (!TableExists("imports"))
        {
            Exec(CreateTableSql);
        }
        else if (!ColumnExists("imports", "broadcast_time"))
        {
            // 最古スキーマ(identity_key 単独 PK)。broadcast_time='' / part_index=0 で移送。
            MigrateVia("''");
        }
        else if (!ColumnExists("imports", "part_index"))
        {
            // 現行スキーマ((identity_key, broadcast_time) PK)。part_index=0 で移送(既存の時刻は保持)。
            MigrateVia("broadcast_time");
        }
        else
        {
            // 最新スキーマ。テーブルはそのまま、インデックスのみ保証する。
            Exec(CreateTableSql);
        }

        // content フィンガープリント列 + 索引(#79 / ADR-0014)を全経路で保証する。追加列は PK に
        // 関与しないため ALTER ADD COLUMN で足せる(rename→copy は不要)。既に列を持つ経路
        // (新規作成 / MigrateVia)では ALTER をスキップし索引だけ保証する。
        EnsureContentColumns();
    }

    // 既存の最新スキーマ(part_index あり・content 列なし = M4 時点の本番台帳)に content 列を後付けする。
    // 追加列は既定 (0,0) = 「内容不明」で埋まり、実ファイル(サイズ > 0)の照会には一致しない(誤スキップ無し)。
    private void EnsureContentColumns()
    {
        if (!ColumnExists("imports", "size_bytes"))
        {
            Exec("ALTER TABLE imports ADD COLUMN size_bytes INTEGER NOT NULL DEFAULT 0;");
            Exec("ALTER TABLE imports ADD COLUMN last_write INTEGER NOT NULL DEFAULT 0;");
        }

        Exec(CreateContentIndexSql);
    }

    // 理由・時刻の列を持たない failures(#172 より前の台帳)に列を後付けする。PK に関与しないので ALTER ADD COLUMN で足せる。
    // 既存の行は理由も時刻も NULL のまま(要対応は「理由の記録なし」〈W-130〉を出す)。
    private void EnsureFailureDetailColumns()
    {
        if (!ColumnExists("failures", "reason"))
            Exec("ALTER TABLE failures ADD COLUMN reason TEXT NULL;");
        if (!ColumnExists("failures", "failed_at"))
            Exec("ALTER TABLE failures ADD COLUMN failed_at TEXT NULL;");
    }

    // 旧 imports を退避し、新スキーマへ移送する。<paramref name="timeExpr"/> は移送元の
    // broadcast_time 相当の式(最古スキーマは列が無いので定数 '')。part_index は既定 0。
    private void MigrateVia(string timeExpr)
    {
        Exec("ALTER TABLE imports RENAME TO imports_legacy;");
        Exec(CreateTableSql);
        Exec(
            $"""
            INSERT INTO imports
                (identity_key, broadcast_time, station, program, broadcast_date,
                 source_path, output_path, rule_version, used_fallback, imported_at)
            SELECT identity_key, {timeExpr}, station, program, broadcast_date,
                   source_path, output_path, rule_version, used_fallback, imported_at
            FROM imports_legacy;
            """);
        Exec("DROP TABLE imports_legacy;");
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private bool TableExists(string name)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1;";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$c LIMIT 1;";
        cmd.Parameters.AddWithValue("$c", column);
        return cmd.ExecuteScalar() is not null;
    }

    // 時刻を "HHmmss" で保持。時刻なし(radikool 等)は空文字("")。ADR-0004 以降は厳密一致
    // のキーの一部であり、ワイルドカードではない(同日重複は part_index で区別する)。
    private static string TimeKey(TimeOnly? time) =>
        time?.ToString("HHmmss", CultureInfo.InvariantCulture) ?? string.Empty;

    public bool Contains(IdentityKey key, TimeOnly? broadcastTime, int partIndex)
    {
        if (!_usable)
            return false;

        using var cmd = _connection.CreateCommand();
        // 厳密一致(ADR-0004)。同日でも時刻 or part が異なれば別放送=未取込。時刻なしの
        // ワイルドカードは撤去した(#11 の粗さの原因。時刻なしソースの同日重複は part で区別する)。
        cmd.CommandText =
            """
            SELECT 1 FROM imports
            WHERE identity_key = $id AND broadcast_time = $t AND part_index = $p
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", key.Value);
        cmd.Parameters.AddWithValue("$t", TimeKey(broadcastTime));
        cmd.Parameters.AddWithValue("$p", partIndex);
        return cmd.ExecuteScalar() is not null;
    }

    public bool ContainsSource(string sourcePath)
    {
        if (!_usable)
            return false;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM imports WHERE source_path = $v LIMIT 1;";
        cmd.Parameters.AddWithValue("$v", sourcePath);
        return cmd.ExecuteScalar() is not null;
    }

    public bool ContainsContent(FileFingerprint fingerprint, DateOnly broadcastDate, TimeOnly? broadcastTime)
    {
        if (!_contentUsable)
            return false;

        using var cmd = _connection.CreateCommand();
        // 取込元パス/番組フォルダに非依存な content ゲート(#79 / ADR-0014)。内容(サイズ + 最終更新)を
        // 放送日 + 放送時刻で裏取りし、別放送の偶発衝突による誤スキップを避ける。part はフォルダ順で
        // 変わりうるのでキーに含めない。
        cmd.CommandText =
            """
            SELECT 1 FROM imports
            WHERE size_bytes = $s AND last_write = $lw
              AND broadcast_date = $d AND broadcast_time = $t
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$s", fingerprint.SizeBytes);
        cmd.Parameters.AddWithValue("$lw", fingerprint.LastWriteUtcTicks);
        cmd.Parameters.AddWithValue("$d", broadcastDate.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$t", TimeKey(broadcastTime));
        return cmd.ExecuteScalar() is not null;
    }

    private const string InsertColumnsSql =
        """
        INTO imports
            (identity_key, broadcast_time, part_index, station, program, broadcast_date, source_path,
             output_path, rule_version, used_fallback, imported_at, size_bytes, last_write)
        VALUES
            ($id, $time, $part, $station, $program, $date, $source, $output, $rule, $fallback, $imported,
             $size, $lw);
        """;

    public void Record(LedgerEntry entry)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE " + InsertColumnsSql;
        BindEntry(cmd, entry);
        cmd.ExecuteNonQuery();
    }

    public bool Replace(LedgerEntry current, LedgerEntry updated)
    {
        // 主キーが変わる更新(再適用。ADR-0016)。UPDATE で PK 列を書き換えると衝突時の扱いが読みにくいので、
        // 旧行の削除と新行の追加を 1 トランザクションで行う。新旧の主キーが同じ(正規名の空白だけが変わった等)
        // でも、削除してから追加するので自分自身とは衝突しない。
        using var tx = _connection.BeginTransaction();

        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText =
                "DELETE FROM imports WHERE identity_key = $id AND broadcast_time = $t AND part_index = $p;";
            delete.Parameters.AddWithValue("$id", current.IdentityKey);
            delete.Parameters.AddWithValue("$t", TimeKey(current.BroadcastTime));
            delete.Parameters.AddWithValue("$p", current.PartIndex);
            if (delete.ExecuteNonQuery() != 1)
            {
                // 書き換える元の記録が無い(別の経路で既に書き換わった等)。何も変えない。
                tx.Rollback();
                return false;
            }
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = tx;
            // INSERT OR IGNORE ではなく素の INSERT。新しい主キーが別の行で使われていたら制約違反で知る
            // (黙って捨てると旧行だけが消えて記録を失う)。
            insert.CommandText = "INSERT " + InsertColumnsSql;
            BindEntry(insert, updated);
            try
            {
                insert.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
            {
                tx.Rollback();
                return false;
            }
        }

        tx.Commit();
        return true;
    }

    private static void BindEntry(SqliteCommand cmd, LedgerEntry entry)
    {
        cmd.Parameters.AddWithValue("$id", entry.IdentityKey);
        cmd.Parameters.AddWithValue("$time", TimeKey(entry.BroadcastTime));
        cmd.Parameters.AddWithValue("$part", entry.PartIndex);
        cmd.Parameters.AddWithValue("$station", entry.Station);
        cmd.Parameters.AddWithValue("$program", entry.Program);
        cmd.Parameters.AddWithValue("$date", entry.BroadcastDate.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$source", entry.SourcePath);
        cmd.Parameters.AddWithValue("$output", entry.OutputPath);
        cmd.Parameters.AddWithValue("$rule", entry.RuleVersion);
        cmd.Parameters.AddWithValue("$fallback", entry.UsedFallback ? 1 : 0);
        cmd.Parameters.AddWithValue("$imported", entry.ImportedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$size", entry.Fingerprint.SizeBytes);
        cmd.Parameters.AddWithValue("$lw", entry.Fingerprint.LastWriteUtcTicks);
    }

    public IReadOnlyList<LedgerEntry> ListAll()
    {
        // 使用不能(互換スキーマでない read-only DB 等)は空。書込側 _usable ガードと同基準。
        if (!_usable)
            return [];

        using var cmd = _connection.CreateCommand();
        // content 列は後付け(#79 / ADR-0014)。read-only で未移行スキーマ(part_index はあるが content 列なし)を
        // 開くと _usable=true でここに来るため、列を無条件 SELECT すると「no such column」で履歴ビューが落ちる。
        // _contentUsable のときだけ列を採り、無ければフィンガープリントは既定 (0,0) にする。
        cmd.CommandText = _contentUsable
            ? """
              SELECT identity_key, broadcast_time, part_index, station, program, broadcast_date,
                     source_path, output_path, rule_version, used_fallback, imported_at,
                     size_bytes, last_write
              FROM imports
              ORDER BY imported_at DESC;
              """
            : """
              SELECT identity_key, broadcast_time, part_index, station, program, broadcast_date,
                     source_path, output_path, rule_version, used_fallback, imported_at
              FROM imports
              ORDER BY imported_at DESC;
              """;

        using var reader = cmd.ExecuteReader();
        var results = new List<LedgerEntry>();
        while (reader.Read())
        {
            var timeRaw = reader.GetString(1); // "HHmmss"、時刻なしは ""(TimeKey の逆)
            results.Add(new LedgerEntry
            {
                IdentityKey = reader.GetString(0),
                BroadcastTime = string.IsNullOrEmpty(timeRaw)
                    ? null
                    : TimeOnly.ParseExact(timeRaw, "HHmmss", CultureInfo.InvariantCulture),
                PartIndex = reader.GetInt32(2),
                Station = reader.GetString(3),
                Program = reader.GetString(4),
                BroadcastDate = DateOnly.ParseExact(reader.GetString(5), DateFormat, CultureInfo.InvariantCulture),
                SourcePath = reader.GetString(6),
                OutputPath = reader.GetString(7),
                RuleVersion = reader.GetString(8),
                UsedFallback = reader.GetInt32(9) != 0,
                ImportedAtUtc = DateTimeOffset.Parse(
                    reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Fingerprint = _contentUsable
                    ? new FileFingerprint(reader.GetInt64(11), reader.GetInt64(12))
                    : default,
            });
        }
        return results;
    }

    // ---- IIngestFailureStore(取込失敗の de-dup。ADR-0011)----

    public bool IsKnown(string sourcePath, FileFingerprint fingerprint)
    {
        if (!_failuresUsable)
            return false;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM failures WHERE source_path = $p AND size_bytes = $s AND last_write = $t LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", sourcePath);
        cmd.Parameters.AddWithValue("$s", fingerprint.SizeBytes);
        cmd.Parameters.AddWithValue("$t", fingerprint.LastWriteUtcTicks);
        return cmd.ExecuteScalar() is not null;
    }

    public void Record(string sourcePath, FileFingerprint fingerprint, string reason, DateTimeOffset failedAtUtc)
    {
        if (!_failuresUsable)
            return;

        // パスが PK。同じパスで内容が変わった(fingerprint 更新)場合は、理由・時刻ごと置き換える。
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR REPLACE INTO failures (source_path, size_bytes, last_write, reason, failed_at)
            VALUES ($p, $s, $t, $r, $at);
            """;
        cmd.Parameters.AddWithValue("$p", sourcePath);
        cmd.Parameters.AddWithValue("$s", fingerprint.SizeBytes);
        cmd.Parameters.AddWithValue("$t", fingerprint.LastWriteUtcTicks);
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.Parameters.AddWithValue("$at", failedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<IngestFailureRecord> List()
    {
        if (!_failuresUsable)
            return [];

        using var cmd = _connection.CreateCommand();
        // 理由・時刻の列は後付け(#172)。read-only で未移行の台帳を開いたときは列を SELECT しない。
        cmd.CommandText = _failureDetailsUsable
            ? "SELECT source_path, reason, failed_at FROM failures;"
            : "SELECT source_path FROM failures;";

        using var reader = cmd.ExecuteReader();
        var results = new List<IngestFailureRecord>();
        while (reader.Read())
        {
            string? reason = null;
            DateTimeOffset? failedAt = null;
            if (_failureDetailsUsable)
            {
                reason = reader.IsDBNull(1) ? null : reader.GetString(1);
                failedAt = reader.IsDBNull(2)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }
            results.Add(new IngestFailureRecord(reader.GetString(0), reason, failedAt));
        }
        return results;
    }

    public void Clear(string sourcePath)
    {
        if (!_failuresUsable)
            return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM failures WHERE source_path = $p;";
        cmd.Parameters.AddWithValue("$p", sourcePath);
        cmd.ExecuteNonQuery();
    }

    // ---- IHeldRecordingStore(保留のストア。ADR-0021)----
    // Clear / List は失敗のストアと名前が重なるので、保留の側を明示的な実装にする(SqliteLedger から呼ぶときは ListHeld)。

    public bool IsHeld(string sourcePath)
    {
        if (!_heldUsable)
            return false;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM held WHERE source_path = $p LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", sourcePath);
        return cmd.ExecuteScalar() is not null;
    }

    public void Record(HeldRecording held)
    {
        if (!_heldUsable)
            return;

        // パスが PK。同じパスを記録し直したら置き換える(呼び手は初めて保留したときだけ記録する)。
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR REPLACE INTO held (source_path, station, folder, filename_program, held_at)
            VALUES ($p, $s, $f, $n, $at);
            """;
        cmd.Parameters.AddWithValue("$p", held.SourcePath);
        cmd.Parameters.AddWithValue("$s", held.Station);
        cmd.Parameters.AddWithValue("$f", held.Folder);
        cmd.Parameters.AddWithValue("$n", held.FilenameProgram);
        cmd.Parameters.AddWithValue("$at", held.HeldAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    void IHeldRecordingStore.Clear(string sourcePath)
    {
        if (!_heldUsable)
            return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM held WHERE source_path = $p;";
        cmd.Parameters.AddWithValue("$p", sourcePath);
        cmd.ExecuteNonQuery();
    }

    IReadOnlyList<HeldRecording> IHeldRecordingStore.List() => ListHeld();

    /// <summary>保留のストアの全件(<see cref="IHeldRecordingStore.List"/>。順序は決めない)。</summary>
    public IReadOnlyList<HeldRecording> ListHeld()
    {
        if (!_heldUsable)
            return [];

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT source_path, station, folder, filename_program, held_at FROM held;";
        using var reader = cmd.ExecuteReader();
        var results = new List<HeldRecording>();
        while (reader.Read())
        {
            results.Add(new HeldRecording(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return results;
    }

    public void Dispose() => _connection.Dispose();
}
