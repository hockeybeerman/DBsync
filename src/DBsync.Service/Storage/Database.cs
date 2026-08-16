using DBsync.Service.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DBsync.Service.Storage;

/// <summary>
/// Owns the SQLite file that backs the activity log, the conflict store, and the per-file sync
/// baseline. Connections are opened per operation and returned to Microsoft.Data.Sqlite's pool;
/// WAL keeps the readers (IPC queries) from blocking the writers (sync workers).
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;
    private readonly ILogger<Database> _log;

    public Database(ILogger<Database> log)
    {
        _log = log;
        ServicePaths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ServicePaths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        Initialise();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout=5000;");
        return connection;
    }

    private void Initialise()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA busy_timeout=5000;");

        Execute(connection, @"
            CREATE TABLE IF NOT EXISTS activity (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                ts        INTEGER NOT NULL,
                kind      INTEGER NOT NULL,
                file      TEXT    NOT NULL,
                pair_id   TEXT    NOT NULL,
                pair_name TEXT    NOT NULL,
                result    INTEGER NOT NULL,
                message   TEXT,
                bytes     INTEGER NOT NULL DEFAULT 0
            );");
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_activity_ts ON activity(ts DESC);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_activity_pair ON activity(pair_id, ts DESC);");

        Execute(connection, @"
            CREATE TABLE IF NOT EXISTS conflict (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                pair_id     TEXT    NOT NULL,
                rel_path    TEXT    NOT NULL,
                detected    INTEGER NOT NULL,
                local_mtime INTEGER NOT NULL DEFAULT 0,
                local_bytes INTEGER NOT NULL DEFAULT 0,
                local_owner TEXT    NOT NULL DEFAULT '',
                share_mtime INTEGER NOT NULL DEFAULT 0,
                share_bytes INTEGER NOT NULL DEFAULT 0,
                share_owner TEXT    NOT NULL DEFAULT '',
                resolved    INTEGER NOT NULL DEFAULT 0,
                resolution  INTEGER
            );");

        // One open conflict per file per pair; re-detection updates the existing row.
        Execute(connection, @"
            CREATE UNIQUE INDEX IF NOT EXISTS ux_conflict_open
                ON conflict(pair_id, rel_path) WHERE resolved = 0;");

        Execute(connection, @"
            CREATE TABLE IF NOT EXISTS file_state (
                pair_id     TEXT    NOT NULL,
                rel_key     TEXT    NOT NULL,
                rel_path    TEXT    NOT NULL,
                local_bytes INTEGER NOT NULL DEFAULT -1,
                local_mtime INTEGER NOT NULL DEFAULT 0,
                share_bytes INTEGER NOT NULL DEFAULT -1,
                share_mtime INTEGER NOT NULL DEFAULT 0,
                synced      INTEGER NOT NULL,
                PRIMARY KEY (pair_id, rel_key)
            );");

        _log.LogInformation("Store ready at {Path}.", ServicePaths.DatabaseFile);
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Drops every trace of a pair: baseline, open conflicts, and optionally its log rows.</summary>
    public void ForgetPair(string pairId, bool includeActivity)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "DELETE FROM file_state WHERE pair_id = $p; DELETE FROM conflict WHERE pair_id = $p;";
            command.Parameters.AddWithValue("$p", pairId);
            command.ExecuteNonQuery();
        }

        if (includeActivity)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM activity WHERE pair_id = $p;";
            command.Parameters.AddWithValue("$p", pairId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
