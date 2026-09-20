using Microsoft.Data.Sqlite;
using ToDoList.Core;

namespace ToDoList.Storage;

public sealed class TaskRepository(string path) : ITaskRepository
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    private Task<T> Run<T>(Func<SqliteConnection, T> action, CancellationToken ct) => Database.RunAsync(Path, () =>
    { using var c = Database.Open(Path); return action(c); }, ct);

    public Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken ct = default) => Run<IReadOnlyList<ProjectInfo>>(c =>
    {
        using var cmd = Database.Command(c, "SELECT Id,Name FROM Projects ORDER BY Name COLLATE NOCASE");
        using var r = cmd.ExecuteReader(); var items = new List<ProjectInfo>();
        while (r.Read()) items.Add(new(r.GetString(0), r.GetString(1)));
        return items;
    }, ct);

    public Task<ProjectInfo> AddProjectAsync(string name, CancellationToken ct = default) => Run(c => InsertProject(c, name), ct);
    private static ProjectInfo InsertProject(SqliteConnection c, string name)
    {
        name = BookRules.ValidateTitle(name);
        if (name is "全部" or "新增") throw new ArgumentException("请使用其他项目名称。");
        var p = new ProjectInfo(Guid.NewGuid().ToString("N"), name);
        try { Database.Exec(c, "INSERT INTO Projects VALUES($id,$name)", ("$id", p.Id), ("$name", p.Name)); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("已有相同名称的项目。", ex); }
        return p;
    }
    public Task<TaskPage> QueryAsync(TaskQuery query, CancellationToken ct = default) => Run(c =>
    {
        using var cmd = BuildQuery(c, query); using var r = cmd.ExecuteReader();
        var rows = new List<TodoItem>();
        while (r.Read()) { ct.ThrowIfCancellationRequested(); rows.Add(Read(r)); }
        var more = rows.Count > query.PageSize;
        if (more) rows.RemoveAt(rows.Count - 1);
        if (query.Direction == PageDirection.Newer) rows.Reverse();
        return new TaskPage(rows, more);
    }, ct);

    public static SqliteCommand BuildQuery(SqliteConnection c, TaskQuery query, bool explain = false)
    {
        if (query.PageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(query.PageSize));
        var conditions = new List<string>(); var args = new List<(string, object?)>();
        if (query.ProjectId != null) { conditions.Add("ProjectId=$project"); args.Add(("$project", query.ProjectId)); }
        if (query.Filter == TaskFilter.Open) conditions.Add("Status<>2");
        if (query.Filter == TaskFilter.Completed) conditions.Add("Status=2");
        if (query.From.HasValue) { conditions.Add("CreatedAt >= $from"); args.Add(("$from", query.From)); }
        if (query.Until.HasValue) { conditions.Add("CreatedAt < $until"); args.Add(("$until", query.Until)); }
        var field = query.ByCompletion ? "CompletedAt" : "CreatedAt";
        var newer = query.Direction == PageDirection.Newer;
        if (query.Cursor != null)
        {
            conditions.Add($"({field},Id) {(newer ? ">" : "<")} ($time,$id)");
            args.Add(("$time", query.Cursor.Time)); args.Add(("$id", query.Cursor.Id));
        }
        args.Add(("$limit", query.PageSize + 1));
        var where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        var direction = newer ? "ASC" : "DESC";
        return Database.Command(c, (explain ? "EXPLAIN QUERY PLAN " : "") +
            $"SELECT Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision FROM Tasks{where} ORDER BY {field} {direction},Id {direction} LIMIT $limit", args.ToArray());
    }
    public static TodoItem Read(SqliteDataReader r) => new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
        r.GetString(2), r.GetString(3), (TodoStatus)r.GetInt32(4), r.GetInt64(5), r.GetInt64(6),
        r.IsDBNull(7) ? null : r.GetInt64(7), r.GetInt64(8));
    private static TodoItem Get(SqliteConnection c, string id)
    {
        using var cmd = Database.Command(c, "SELECT Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision FROM Tasks WHERE Id=$id", ("$id", id));
        using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : throw new InvalidOperationException("任务已不存在，请刷新列表。");
    }
    private static void ValidateContent(RichContent content)
    {
        RichContent.Parse(content.ToJson());
        if (string.IsNullOrWhiteSpace(content.PlainText)) throw new ArgumentException("请先写下要做的事情。");
    }
    public Task<TodoItem> AddTaskAsync(RichContent content, string? projectId, string? newProject = null, CancellationToken ct = default) => Run(c =>
    {
        ValidateContent(content);
        using var tx = c.BeginTransaction();
        if (newProject != null) projectId = InsertProject(c, newProject).Id;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var item = new TodoItem(Guid.NewGuid().ToString("N"), projectId, content.ToJson(), content.PlainText, TodoStatus.Open, now, now, null, 1);
        Insert(c, item); tx.Commit(); return item;
    }, ct);
    public static void Insert(SqliteConnection c, TodoItem item) => Database.Exec(c,
        "INSERT INTO Tasks(Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision) VALUES($id,$project,$json,$text,$status,$created,$updated,$completed,$revision)", Args(item));
    public static (string, object?)[] Args(TodoItem t) => [("$id", t.Id), ("$project", t.ProjectId), ("$json", t.ContentJson),
        ("$text", t.PlainText), ("$status", (int)t.Status), ("$created", t.CreatedAt), ("$updated", t.UpdatedAt),
        ("$completed", t.CompletedAt), ("$revision", t.Revision)];
    public Task<TodoItem> UpdateContentAsync(string id, RichContent content, long revision, CancellationToken ct = default) => Run(c =>
    {
        ValidateContent(content); using var tx = c.BeginTransaction(); var old = Get(c, id);
        if (old.Revision != revision) throw new InvalidOperationException("任务已更新，请取消编辑后重新打开。");
        var now = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), old.UpdatedAt + 1);
        Database.Exec(c, "UPDATE Tasks SET ContentJson=$json,PlainText=$text,UpdatedAt=$now,Revision=Revision+1 WHERE Id=$id",
            ("$json", content.ToJson()), ("$text", content.PlainText), ("$now", now), ("$id", id));
        var item = Get(c, id); tx.Commit(); return item;
    }, ct);
    public Task<TodoItem> SetStatusAsync(string id, TodoStatus status, long revision, CancellationToken ct = default) => Run(c =>
    {
        if (!Enum.IsDefined(status)) throw new ArgumentException("未知任务状态。");
        using var tx = c.BeginTransaction(); var old = Get(c, id);
        if (old.Revision != revision) throw new InvalidOperationException("任务状态已更新，请重试。");
        if (old.Status == status) return old;
        var now = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), old.UpdatedAt + 1);
        Database.Exec(c, "UPDATE Tasks SET Status=$status,CompletedAt=$completed,UpdatedAt=$now,Revision=Revision+1 WHERE Id=$id",
            ("$status", (int)status), ("$completed", status == TodoStatus.Completed ? now : null), ("$now", now), ("$id", id));
        Database.Exec(c, "INSERT INTO TaskStateEvents VALUES($event,$task,$from,$to,$now)",
            ("$event", Guid.NewGuid().ToString("N")), ("$task", id), ("$from", (int)old.Status), ("$to", (int)status), ("$now", now));
        var item = Get(c, id); tx.Commit(); return item;
    }, ct);
    public Task<string?> GetSettingAsync(string key) => Run(c => Database.Scalar(c,
        "SELECT Value FROM BookSettings WHERE Key=$key", ("$key", key)) as string, default);
    public Task SetSettingAsync(string key, string value) => Run(c => Database.Exec(c,
        "INSERT INTO BookSettings VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", ("$key", key), ("$value", value)), default);
}
