using System.Globalization;
using Microsoft.Data.Sqlite;
using Radirec.Core.Model;

namespace Radirec.Core.Ledger;

/// <summary>
/// SQLite(WAL)によるローカルディスク台帳。SMB(出力先)には置かない前提。
/// </summary>
public sealed class SqliteLedger : ILedger, IDisposable
{
    private const string DateFormat = "yyyy-MM-dd";
    private readonly SqliteConnection _connection;

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
        }.ToString());
        _connection.Open();

        if (!readOnly)
            Initialize();
    }

    private void Initialize()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS imports (
                identity_key   TEXT PRIMARY KEY,
                station        TEXT NOT NULL,
                program        TEXT NOT NULL,
                broadcast_date TEXT NOT NULL,
                source_path    TEXT NOT NULL,
                output_path    TEXT NOT NULL,
                rule_version   TEXT NOT NULL,
                used_fallback  INTEGER NOT NULL,
                imported_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_imports_source ON imports(source_path);
            """;
        cmd.ExecuteNonQuery();
    }

    public bool Contains(IdentityKey key) => Exists("identity_key", key.Value);

    public bool ContainsSource(string sourcePath) => Exists("source_path", sourcePath);

    private bool Exists(string column, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM imports WHERE {column} = $v LIMIT 1;";
        cmd.Parameters.AddWithValue("$v", value);
        return cmd.ExecuteScalar() is not null;
    }

    public void Record(LedgerEntry entry)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR IGNORE INTO imports
                (identity_key, station, program, broadcast_date, source_path,
                 output_path, rule_version, used_fallback, imported_at)
            VALUES
                ($id, $station, $program, $date, $source, $output, $rule, $fallback, $imported);
            """;
        cmd.Parameters.AddWithValue("$id", entry.IdentityKey);
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

    public void Dispose() => _connection.Dispose();
}
