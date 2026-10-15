using Microsoft.Data.Sqlite;

namespace ToDoList.Storage;

// Connection-local triggers capture only touched rows, including bulk module changes.
// Neither the journal nor the one-shot receipt is persisted in the user's database.
public sealed class LocalUndo
{
    private sealed record Table(string Name, string Kind, string[] Columns);
    private sealed record Change(Table Table, string Id, object?[]? Before, object?[]? After);
    private static readonly Table[] Tables =
    [
        new("Projects", "project", ["Id", "Name", "SortOrder"]),
        new("Tasks", "task", ["Id", "ProjectId", "ContentJson", "PlainText", "Status", "CreatedAt", "UpdatedAt", "CompletedAt", "Revision", "DeletedAt", "PreviousStatus", "PreviousCompletedAt", "EditedAt"]),
        new("TaskStateEvents", "event", ["Id", "TaskId", "FromStatus", "ToStatus", "OccurredAt"])
    ];
    private readonly Change[] _changes;
    private int _used;
    public string Path { get; }
    public string Message { get; }
    public IReadOnlySet<string> Kinds { get; }
    public IReadOnlyList<(string Kind, string Id)> Records => _changes.Select(c => (c.Table.Kind, c.Id)).ToArray();
    private LocalUndo(string path, string message, Change[] changes)
    { Path = path; Message = message; _changes = changes; Kinds = changes.Select(c => c.Table.Kind).ToHashSet(); }

    internal static void Begin(SqliteConnection c)
    {
        foreach (var table in Tables)
        {
            var name = table.Name; var columns = string.Join(',', table.Columns);
            Database.Exec(c, $"CREATE TEMP TABLE Undo{name} AS SELECT 1 AS HadRow,{columns} FROM {name} WHERE 0; CREATE UNIQUE INDEX Undo{name}Id ON Undo{name}(Id);");
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var values = operation == "INSERT" ? "0,NEW.Id," + string.Join(',', table.Columns.Skip(1).Select(_ => "NULL"))
                    : "1," + string.Join(',', table.Columns.Select(column => "OLD." + column));
                Database.Exec(c, $"CREATE TEMP TRIGGER Record{name}{operation} BEFORE {operation} ON main.{name} BEGIN INSERT OR IGNORE INTO Undo{name} SELECT {values}; END;");
            }
        }
    }
    internal static LocalUndo? Capture(SqliteConnection c, string path, string message)
    {
        var changes = new List<Change>();
        foreach (var table in Tables)
        {
            var originals = new List<(string Id, object?[]? Row)>();
            using (var cmd = Database.Command(c, $"SELECT HadRow,{string.Join(',', table.Columns)} FROM Undo{table.Name}"))
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) originals.Add((reader.GetString(1), reader.GetInt64(0) == 0 ? null : ReadRow(reader, 1)));
            foreach (var (id, before) in originals)
            {
                var after = Get(c, table, id);
                if (!Equal(before, after)) changes.Add(new(table, id, before, after));
            }
        }
        return changes.Count == 0 ? null : new(path, message, changes.ToArray());
    }
    private static object?[] ReadRow(SqliteDataReader reader, int start = 0) => Enumerable.Range(start, reader.FieldCount - start)
        .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray();
    private static object?[]? Get(SqliteConnection c, Table table, string id)
    {
        using var cmd = Database.Command(c, $"SELECT {string.Join(',', table.Columns)} FROM {table.Name} WHERE Id=$id", ("$id", id));
        using var reader = cmd.ExecuteReader(); return reader.Read() ? ReadRow(reader) : null;
    }
    private static bool Equal(object?[]? a, object?[]? b) => a == null || b == null ? a == b : a.SequenceEqual(b);
    private static bool Matches(Change change, object?[]? actual)
    {
        if (change.Table.Kind != "task" || actual == null || change.After == null) return Equal(change.After, actual);
        // Sync may replace bookkeeping without changing the actual operation.
        return Enumerable.Range(0, actual.Length).Where(i => i is not (6 or 8)).All(i => Equals(actual[i], change.After[i]));
    }
    public Task UndoAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0) throw new InvalidOperationException("本次操作已撤回或撤回已受理。");
        return Database.RunAsync(Path, () =>
        {
            using var c = Database.Open(Path); using var tx = c.BeginTransaction();
            foreach (var change in _changes)
            {
                ct.ThrowIfCancellationRequested();
                if (!Matches(change, Get(c, change.Table, change.Id))) throw new InvalidOperationException("相关数据已经发生变化，无法撤回；未修改任何数据。");
            }
            // Restore containers first, then tasks and their events. Delete in reverse order.
            foreach (var table in Tables)
                foreach (var change in _changes.Where(ch => ch.Table == table && ch.Before != null))
                {
                    var row = change.Before!.ToArray();
                    if (table.Kind == "task")
                    {
                        var current = Get(c, table, change.Id);
                        row[6] = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Convert.ToInt64(current?[6] ?? row[6]) + 1);
                        row[8] = Convert.ToInt64(current?[8] ?? row[8]) + 1;
                    }
                    var args = row.Select((value, index) => ("$v" + index, value)).ToArray();
                    Database.Exec(c, $"INSERT INTO {table.Name}({string.Join(',', table.Columns)}) VALUES({string.Join(',', args.Select(a => a.Item1))}) ON CONFLICT(Id) DO UPDATE SET {string.Join(',', table.Columns.Skip(1).Select(col => col + "=excluded." + col))}", args);
                    Database.Exec(c, "DELETE FROM SyncTombstones WHERE Kind=$kind AND EntityId=$id", ("$kind", table.Kind), ("$id", change.Id));
                }
            foreach (var table in Tables.Reverse())
                foreach (var change in _changes.Where(ch => ch.Table == table && ch.Before == null))
                {
                    Database.Exec(c, $"DELETE FROM {table.Name} WHERE Id=$id", ("$id", change.Id));
                    Database.Exec(c, "INSERT OR IGNORE INTO SyncTombstones VALUES($kind,$id)", ("$kind", table.Kind), ("$id", change.Id));
                }
            ct.ThrowIfCancellationRequested(); tx.Commit(); Database.NotifyChanged(Path); return true;
        }, ct);
    }
}
