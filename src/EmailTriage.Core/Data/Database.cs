using Microsoft.Data.Sqlite;

namespace EmailTriage.Core.Data;

/// <summary>
/// Owns the SQLite file and its schema. Everything the app knows that Outlook
/// does not - notes, blockers, assignments, snooze times, folder habits - lives
/// here. Outlook itself stays the source of truth for the mail.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public string Path { get; }

    public Database(string path)
    {
        Path = path;

        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    /// <summary>Default location: %LOCALAPPDATA%\EmailTriage\triage.db</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmailTriage",
            "triage.db");

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var pragma = conn.CreateCommand();
        // WAL keeps the UI readable while the snooze loop writes.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return conn;
    }

    public void Migrate()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        Exec(conn, tx, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");

        int current;
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            current = Convert.ToInt32(read.ExecuteScalar() ?? 0);
        }

        foreach (var (version, sql) in Migrations)
        {
            if (version <= current) continue;

            Exec(conn, tx, sql);

            using var stamp = conn.CreateCommand();
            stamp.Transaction = tx;
            stamp.CommandText = "INSERT INTO schema_version (version) VALUES ($v);";
            stamp.Parameters.AddWithValue("$v", version);
            stamp.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Ordered schema steps. Append only - never edit a step that has shipped.
    /// </summary>
    private static readonly (int Version, string Sql)[] Migrations =
    {
        (1, """
            CREATE TABLE action_items (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                internet_message_id TEXT    NOT NULL UNIQUE,
                entry_id            TEXT    NOT NULL DEFAULT '',
                store_id            TEXT    NOT NULL DEFAULT '',
                subject             TEXT    NOT NULL DEFAULT '',
                sender_name         TEXT    NOT NULL DEFAULT '',
                sender_address      TEXT    NOT NULL DEFAULT '',
                received_utc        TEXT    NOT NULL,
                created_utc         TEXT    NOT NULL,
                completed_utc       TEXT    NULL,
                priority            INTEGER NOT NULL DEFAULT 1,
                notes               TEXT    NOT NULL DEFAULT ''
            );

            CREATE INDEX ix_action_items_open ON action_items (completed_utc, priority DESC);

            CREATE TABLE blocking_tasks (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                action_item_id  INTEGER NOT NULL REFERENCES action_items(id) ON DELETE CASCADE,
                description     TEXT    NOT NULL,
                waiting_on      TEXT    NOT NULL DEFAULT '',
                due_utc         TEXT    NULL,
                created_utc     TEXT    NOT NULL,
                resolved_utc    TEXT    NULL
            );

            CREATE INDEX ix_blocking_tasks_parent ON blocking_tasks (action_item_id);

            CREATE TABLE assignments (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                action_item_id  INTEGER NOT NULL REFERENCES action_items(id) ON DELETE CASCADE,
                person_name     TEXT    NOT NULL,
                person_email    TEXT    NOT NULL DEFAULT '',
                task            TEXT    NOT NULL,
                due_utc         TEXT    NULL,
                created_utc     TEXT    NOT NULL,
                done_utc        TEXT    NULL,
                notified_utc    TEXT    NULL
            );

            CREATE INDEX ix_assignments_parent ON assignments (action_item_id);

            CREATE TABLE snoozes (
                id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                internet_message_id     TEXT    NOT NULL DEFAULT '',
                entry_id                TEXT    NOT NULL,
                store_id                TEXT    NOT NULL,
                subject                 TEXT    NOT NULL DEFAULT '',
                sender_name             TEXT    NOT NULL DEFAULT '',
                origin_folder_entry_id  TEXT    NOT NULL DEFAULT '',
                origin_folder_store_id  TEXT    NOT NULL DEFAULT '',
                origin_folder_path      TEXT    NOT NULL DEFAULT '',
                snoozed_utc             TEXT    NOT NULL,
                return_utc              TEXT    NOT NULL,
                restored_utc            TEXT    NULL,
                last_error              TEXT    NOT NULL DEFAULT '',
                failure_count           INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX ix_snoozes_pending ON snoozes (restored_utc, return_utc);

            CREATE TABLE folder_usage (
                folder_path  TEXT    PRIMARY KEY,
                use_count    INTEGER NOT NULL DEFAULT 0,
                last_used_utc TEXT   NOT NULL
            );
            """),
    };
}
