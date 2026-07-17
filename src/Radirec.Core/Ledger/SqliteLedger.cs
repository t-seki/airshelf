using System.Globalization;
using Microsoft.Data.Sqlite;
using Radirec.Core.Ingestion;
using Radirec.Core.Model;

namespace Radirec.Core.Ledger;

/// <summary>
/// SQLite(WAL)によるローカルディスク台帳。SMB(出力先)には置かない前提。取込済み記録(import-once)に
/// 加えて、取込失敗の de-dup ストア(<see cref="IIngestFailureStore"/>。ADR-0011)を同一 DB の別テーブルで
/// 相乗りさせる(Windows の SQLite ファイルロックを増やさないため別ファイルにしない)。
/// </summary>
public sealed class SqliteLedger : ILedger, IIngestFailureStore, IDisposable
{
    private const string DateFormat = "yyyy-MM-dd";

    // 取込失敗の de-dup テーブル。鍵 = ソースパス。フィンガープリント(サイズ + 最終更新 ticks)が
    // 一致するときだけ「通知済み」とみなす(内容変化で再通知)。
    private const string CreateFailuresTableSql =
        """
        CREATE TABLE IF NOT EXISTS failures (
            source_path TEXT NOT NULL PRIMARY KEY,
            size_bytes  INTEGER NOT NULL,
            last_write  INTEGER NOT NULL
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
            PRIMARY KEY (identity_key, broadcast_time, part_index)
        );
        CREATE INDEX IF NOT EXISTS ix_imports_source ON imports(source_path);
        """;

    private readonly SqliteConnection _connection;

    // 読み取り専用で開いた際、互換スキーマ(part_index 列あり)でなければ台帳を使わない。
    // 旧スキーマDBに対する dry-run が例外(no such column: part_index 等)で落ちるのを防ぐ
    // (readOnly では作り直せないため)。
    private readonly bool _usable;

    // failures テーブルが使えるか(readOnly の旧 DB でテーブル欠如時に throw させないためのガード)。
    private readonly bool _failuresUsable;

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
                _usable = true;
                _failuresUsable = true;
            }
            else
            {
                _usable = TableExists("imports") && ColumnExists("imports", "part_index");
                _failuresUsable = TableExists("failures");
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
            return;
        }

        if (!ColumnExists("imports", "broadcast_time"))
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

    public void Record(LedgerEntry entry)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR IGNORE INTO imports
                (identity_key, broadcast_time, part_index, station, program, broadcast_date, source_path,
                 output_path, rule_version, used_fallback, imported_at)
            VALUES
                ($id, $time, $part, $station, $program, $date, $source, $output, $rule, $fallback, $imported);
            """;
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
        cmd.ExecuteNonQuery();
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

    public void Record(string sourcePath, FileFingerprint fingerprint)
    {
        if (!_failuresUsable)
            return;

        // パスが PK。同じパスで内容が変わった(fingerprint 更新)場合は置き換える。
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR REPLACE INTO failures (source_path, size_bytes, last_write)
            VALUES ($p, $s, $t);
            """;
        cmd.Parameters.AddWithValue("$p", sourcePath);
        cmd.Parameters.AddWithValue("$s", fingerprint.SizeBytes);
        cmd.Parameters.AddWithValue("$t", fingerprint.LastWriteUtcTicks);
        cmd.ExecuteNonQuery();
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

    public void Dispose() => _connection.Dispose();
}
