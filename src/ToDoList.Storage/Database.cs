using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace ToDoList.Storage;

public static class Database
{
    public const int Version = 2;
    public const int ApplicationId = 0x544F444F;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public static async Task<T> RunAsync<T>(string path, Func<T> action, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(System.IO.Path.GetFullPath(path), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => { ct.ThrowIfCancellationRequested(); return action(); }, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            c.Open();
            Exec(c, "PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA busy_timeout=5000;");
            return c;
        }
        catch { c.Dispose(); throw; }
    }
    public static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    public static int Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    { using var cmd = Command(c, sql, args); return cmd.ExecuteNonQuery(); }
    public static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    { using var cmd = Command(c, sql, args); return cmd.ExecuteScalar(); }
    public static void Create(string path, string name, string title)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        using var tx = c.BeginTransaction();
        Exec(c, Schema);
        Exec(c, "INSERT INTO BookMetadata VALUES(1,$name,$title);", ("$name", name), ("$title", title));
        Exec(c, $"PRAGMA user_version={Version}; PRAGMA application_id={ApplicationId};");
        tx.Commit();
    }
    public static void Snapshot(string source, string destination)
    {
        using var from = Open(source, true);
        using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        to.Open(); from.BackupDatabase(to);
        Exec(to, "PRAGMA journal_mode=DELETE;");
    }
    public static void CleanupStaging(string path)
    {
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(p)) File.Delete(p);
    }
    public static void Upgrade(string path, string? backupDirectory)
    {
        using var c = Open(path);
        if (Convert.ToInt32(Scalar(c, "PRAGMA application_id")) != ApplicationId)
            throw new InvalidDataException("这不是 ToDoList 待办书。");
        var version = Convert.ToInt32(Scalar(c, "PRAGMA user_version"));
        if (version == Version) return;
        if (version != 1) throw new InvalidDataException($"不支持待办书格式版本 {version}。");
        if (!Equals(Scalar(c, "PRAGMA quick_check"), "ok")) throw new InvalidDataException("数据库完整性检查失败。");
        if (Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM sqlite_schema WHERE type IN ('view','trigger')")) != 0)
            throw new InvalidDataException("待办书包含不支持的数据库对象。");
        if (backupDirectory != null)
        {
            Directory.CreateDirectory(backupDirectory);
            Snapshot(path, System.IO.Path.Combine(backupDirectory, $"{System.IO.Path.GetFileNameWithoutExtension(path)}-v1-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db"));
        }
        Exec(c, "PRAGMA foreign_keys=OFF;");
        try
        {
            using var tx = c.BeginTransaction();
            Exec(c, "ALTER TABLE Tasks RENAME TO Tasks_v1; ALTER TABLE TaskStateEvents RENAME TO Events_v1;");
            var indexes = new List<string>();
            using (var cmd = Command(c, "SELECT name FROM sqlite_schema WHERE type='index' AND tbl_name IN ('Tasks_v1','Events_v1') AND sql IS NOT NULL"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) indexes.Add(r.GetString(0));
            foreach (var index in indexes) Exec(c, "DROP INDEX \"" + index.Replace("\"", "\"\"") + "\";");
            // Create only the two changed tables and their indexes.
            var tables = Schema[Schema.IndexOf("CREATE TABLE Tasks(", StringComparison.Ordinal)..];
            tables = tables.Replace("CREATE TABLE BookSettings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);", "");
            Exec(c, tables);
            Exec(c, "INSERT INTO Tasks(Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision) SELECT Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision FROM Tasks_v1; INSERT INTO TaskStateEvents SELECT * FROM Events_v1; DROP TABLE Events_v1; DROP TABLE Tasks_v1;");
            using (var cmd = Command(c, "PRAGMA foreign_key_check"))
            using (var r = cmd.ExecuteReader()) if (r.Read()) throw new InvalidDataException("升级失败：任务关联不完整。");
            Exec(c, $"PRAGMA user_version={Version};");
            tx.Commit();
        }
        finally { Exec(c, "PRAGMA foreign_keys=ON;"); }
    }
    // Migration 1. Future migrations must run transactionally after checking application_id/user_version.
    public const string Schema = """
        CREATE TABLE BookMetadata(Id INTEGER PRIMARY KEY CHECK(Id=1), Name TEXT NOT NULL, Title TEXT NOT NULL);
        CREATE TABLE Projects(Id TEXT PRIMARY KEY, Name TEXT NOT NULL COLLATE NOCASE UNIQUE);
        CREATE TABLE Tasks(
          Id TEXT PRIMARY KEY, ProjectId TEXT REFERENCES Projects(Id), ContentJson TEXT NOT NULL,
          PlainText TEXT NOT NULL, Status INTEGER NOT NULL CHECK(Status IN (0,1,2,3)),
          CreatedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL, CompletedAt INTEGER,
          Revision INTEGER NOT NULL DEFAULT 1 CHECK(Revision>0),
          DeletedAt INTEGER, PreviousStatus INTEGER, PreviousCompletedAt INTEGER,
          CHECK((Status=3 AND DeletedAt IS NOT NULL AND PreviousStatus IS NOT NULL AND PreviousStatus IN(0,1,2)
            AND ((PreviousStatus=2 AND PreviousCompletedAt IS NOT NULL) OR (PreviousStatus<>2 AND PreviousCompletedAt IS NULL)))
            OR (Status<>3 AND DeletedAt IS NULL AND PreviousStatus IS NULL AND PreviousCompletedAt IS NULL)),
          CHECK((Status=2 AND CompletedAt IS NOT NULL) OR (Status<>2 AND CompletedAt IS NULL)));
        CREATE TABLE TaskStateEvents(Id TEXT PRIMARY KEY, TaskId TEXT NOT NULL REFERENCES Tasks(Id),
          FromStatus INTEGER NOT NULL CHECK(FromStatus IN (0,1,2,3)),
          ToStatus INTEGER NOT NULL CHECK(ToStatus IN (0,1,2,3)), OccurredAt INTEGER NOT NULL);
        CREATE TABLE BookSettings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
        CREATE INDEX ix_created ON Tasks(CreatedAt,Id);
        CREATE INDEX ix_project_created ON Tasks(ProjectId,CreatedAt,Id);
        CREATE INDEX ix_open_created ON Tasks(CreatedAt,Id) WHERE Status IN(0,1);
        CREATE INDEX ix_open_project_created ON Tasks(ProjectId,CreatedAt,Id) WHERE Status IN(0,1);
        CREATE INDEX ix_done_created ON Tasks(CreatedAt,Id) WHERE Status=2;
        CREATE INDEX ix_done_project_created ON Tasks(ProjectId,CreatedAt,Id) WHERE Status=2;
        CREATE INDEX ix_done_completed ON Tasks(CompletedAt,Id) WHERE Status=2;
        CREATE INDEX ix_done_project_completed ON Tasks(ProjectId,CompletedAt,Id) WHERE Status=2;
        CREATE INDEX ix_deleted ON Tasks(DeletedAt,Id) WHERE Status=3;
        CREATE INDEX ix_project_deleted ON Tasks(ProjectId,DeletedAt,Id) WHERE Status=3;
        CREATE INDEX ix_events_task ON TaskStateEvents(TaskId,OccurredAt);
        """;
}
