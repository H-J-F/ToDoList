using Microsoft.Data.Sqlite;
using ToDoList.Core;

namespace ToDoList.Storage;

public sealed class TaskRepository(string path) : ITaskRepository
{
    public Task PermanentlyDeleteAsync(IReadOnlyDictionary<string, long> revisions, CancellationToken ct = default)
    {
        var requested = revisions.ToArray();
        return Run(c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var (id, revision) in requested)
            {
                ct.ThrowIfCancellationRequested();
                var item = Get(c, id);
                if (item.Status != TodoStatus.Deleted || item.Revision != revision)
                    throw new InvalidOperationException("所选任务已变更，请刷新后重新选择；未删除任何任务。");
                Database.Exec(c, "DELETE FROM TaskStateEvents WHERE TaskId=$id", ("$id", id));
                Database.Exec(c, "DELETE FROM Tasks WHERE Id=$id", ("$id", id));
            }
            tx.Commit(); return true;
        }, ct);
    }
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    private Task<T> Run<T>(Func<SqliteConnection, T> action, CancellationToken ct) => Database.RunAsync(Path, () =>
    { using var c = Database.Open(Path); return action(c); }, ct);

    public Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken ct = default) => Run<IReadOnlyList<ProjectInfo>>(c =>
    {
        using var cmd = Database.Command(c, "SELECT Id,Name,SortOrder FROM Projects ORDER BY SortOrder,Name COLLATE NOCASE,Id");
        using var r = cmd.ExecuteReader(); var items = new List<ProjectInfo>();
        while (r.Read()) items.Add(new(r.GetString(0), r.GetString(1), r.GetInt32(2)));
        return items;
    }, ct);

    public Task<ProjectInfo> AddProjectAsync(string name, CancellationToken ct = default) => Run(c => InsertProject(c, name), ct);
    public Task<ProjectInfo> RenameProjectAsync(string id, string name, CancellationToken ct = default) => Run(c =>
    {
        name = BookRules.ValidateTitle(name);
        if (name is "全部" or "新增") throw new ArgumentException("请使用其他模块名称。");
        if (Convert.ToInt64(Database.Scalar(c, "SELECT COUNT(*) FROM Projects WHERE Id=$id", ("$id", id))) == 0)
            throw new ArgumentException("所选模块已不存在。");
        try { Database.Exec(c, "UPDATE Projects SET Name=$name WHERE Id=$id", ("$name", name), ("$id", id)); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("已有相同名称的模块。", ex); }
        return ReadProject(c, id);
    }, ct);
    public Task DeleteProjectAsync(string id, CancellationToken ct = default) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        if (Convert.ToInt64(Database.Scalar(c, "SELECT COUNT(*) FROM Projects WHERE Id=$id", ("$id", id))) == 0)
            throw new ArgumentException("所选模块已不存在。");
        Database.Exec(c, "UPDATE Tasks SET ProjectId=NULL WHERE ProjectId=$id", ("$id", id));
        Database.Exec(c, "DELETE FROM Projects WHERE Id=$id", ("$id", id));
        NormalizeProjectOrder(c); tx.Commit();
        return true;
    }, ct);
    public Task ReorderProjectsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        var existing = Convert.ToInt32(Database.Scalar(c, "SELECT COUNT(*) FROM Projects"));
        var distinct = orderedIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length != existing || distinct.Any(id => Convert.ToInt64(Database.Scalar(c, "SELECT COUNT(*) FROM Projects WHERE Id=$id", ("$id", id))) == 0))
            throw new ArgumentException("模块顺序包含无效模块。");
        for (var i = 0; i < distinct.Length; i++) Database.Exec(c, "UPDATE Projects SET SortOrder=$order WHERE Id=$id", ("$order", i), ("$id", distinct[i]));
        tx.Commit(); return true;
    }, ct);
    public Task<ReportSnapshot> ReadReportAsync(ReportOptions options, CancellationToken ct = default) => Run(c =>
    {
        var query = options.Query();
        using var tx = c.BeginTransaction(deferred: true);
        var book = BookLibrary.ReadInfo(c, Path);
        var projects = new List<ProjectInfo>();
        using (var cmd = Database.Command(c, "SELECT Id,Name,SortOrder FROM Projects ORDER BY SortOrder,Name,Id"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) { ct.ThrowIfCancellationRequested(); projects.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2))); }
        if (options.ProjectId != null && !projects.Any(p => p.Id == options.ProjectId)) throw new ArgumentException("所选模块已不存在。");
        using var tasksCommand = BuildQuery(c, query, forReport: true);
        var tasks = new List<TodoItem>();
        using (var reader = tasksCommand.ExecuteReader())
            while (reader.Read()) { ct.ThrowIfCancellationRequested(); tasks.Add(Read(reader)); }
        var colors = new Dictionary<string, string>();
        using (var settings = Database.Command(c, "SELECT Key,Value FROM BookSettings WHERE Key LIKE 'StatusColor.%'"))
        using (var reader = settings.ExecuteReader()) while (reader.Read()) colors[reader.GetString(0)] = reader.GetString(1);
        tx.Commit();
        return new ReportSnapshot(book.Title, projects, tasks, colors);
    }, ct);
    private static ProjectInfo InsertProject(SqliteConnection c, string name)
    {
        name = BookRules.ValidateTitle(name);
        if (name is "全部" or "新增") throw new ArgumentException("请使用其他模块名称。");
        var p = new ProjectInfo(Guid.NewGuid().ToString("N"), name, Convert.ToInt32(Database.Scalar(c, "SELECT COALESCE(MAX(SortOrder),-1)+1 FROM Projects")));
        try { Database.Exec(c, "INSERT INTO Projects(Id,Name,SortOrder) VALUES($id,$name,$order)", ("$id", p.Id), ("$name", p.Name), ("$order", p.SortOrder)); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("已有相同名称的模块。", ex); }
        return p;
    }
    private static ProjectInfo ReadProject(SqliteConnection c, string id)
    {
        using var cmd = Database.Command(c, "SELECT Id,Name,SortOrder FROM Projects WHERE Id=$id", ("$id", id));
        using var r = cmd.ExecuteReader(); if (!r.Read()) throw new ArgumentException("所选模块已不存在。");
        return new(r.GetString(0), r.GetString(1), r.GetInt32(2));
    }
    private static void NormalizeProjectOrder(SqliteConnection c)
    {
        using var cmd = Database.Command(c, "SELECT Id FROM Projects ORDER BY SortOrder,Name,Id");
        using var r = cmd.ExecuteReader(); var ids = new List<string>(); while (r.Read()) ids.Add(r.GetString(0));
        for (var i = 0; i < ids.Count; i++) Database.Exec(c, "UPDATE Projects SET SortOrder=$order WHERE Id=$id", ("$order", i), ("$id", ids[i]));
    }
    public Task<TaskPage> QueryAsync(TaskQuery query, CancellationToken ct = default) => Run(c =>
    {
        using var cmd = BuildQuery(c, query); using var r = cmd.ExecuteReader();
        var rows = new List<TodoItem>();
        while (r.Read()) { ct.ThrowIfCancellationRequested(); rows.Add(Read(r)); }
        var more = rows.Count > query.PageSize;
        if (more) rows.RemoveAt(rows.Count - 1);
        if (query.Direction == PageDirection.Older) rows.Reverse();
        return new TaskPage(rows, more);
    }, ct);

    public static SqliteCommand BuildQuery(SqliteConnection c, TaskQuery query, bool explain = false, bool forReport = false)
    {
        if (query.PageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(query.PageSize));
        var conditions = new List<string>(); var args = new List<(string, object?)>();
        if (query.ProjectId != null) { conditions.Add("ProjectId=$project"); args.Add(("$project", query.ProjectId)); }
        if (query.Filter == TaskFilter.Open) conditions.Add("Status IN(0,1)");
        if (query.Filter == TaskFilter.Deleted) conditions.Add("Status=3");
        if (query.Filter == TaskFilter.Completed) conditions.Add("Status=2");
        if (query.From.HasValue) { conditions.Add("CreatedAt >= $from"); args.Add(("$from", query.From)); }
        if (query.Until.HasValue) { conditions.Add("CreatedAt < $until"); args.Add(("$until", query.Until)); }
        var field = forReport ? "CreatedAt" : query.ByDeletion ? "DeletedAt" : query.ByCompletion ? "CompletedAt" : "CreatedAt";
        var newer = forReport || query.Direction == PageDirection.Newer;
        if (!forReport && query.Cursor != null)
        {
            conditions.Add($"({field},Id) {(newer ? ">" : "<")}{(query.IncludeCursor ? "=" : "")} ($time,$id)");
            args.Add(("$time", query.Cursor.Time)); args.Add(("$id", query.Cursor.Id));
        }
        if (!forReport) args.Add(("$limit", query.PageSize + 1));
        var where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        var direction = newer ? "ASC" : "DESC";
        return Database.Command(c, (explain ? "EXPLAIN QUERY PLAN " : "") +
            $"SELECT Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision,DeletedAt,PreviousStatus,PreviousCompletedAt FROM Tasks{where} ORDER BY {field} {direction},Id {direction}" + (forReport ? "" : " LIMIT $limit"), args.ToArray());
    }
    public static TodoItem Read(SqliteDataReader r) => new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
        r.GetString(2), r.GetString(3), (TodoStatus)r.GetInt32(4), r.GetInt64(5), r.GetInt64(6),
        r.IsDBNull(7) ? null : r.GetInt64(7), r.GetInt64(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : (TodoStatus)r.GetInt32(10), r.IsDBNull(11) ? null : r.GetInt64(11));
    private static TodoItem Get(SqliteConnection c, string id)
    {
        using var cmd = Database.Command(c, "SELECT Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision,DeletedAt,PreviousStatus,PreviousCompletedAt FROM Tasks WHERE Id=$id", ("$id", id));
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
        "INSERT INTO Tasks(Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision,DeletedAt,PreviousStatus,PreviousCompletedAt) VALUES($id,$project,$json,$text,$status,$created,$updated,$completed,$revision,$deleted,$previous,$previousCompleted)", Args(item));
    public static (string, object?)[] Args(TodoItem t) => [("$id", t.Id), ("$project", t.ProjectId), ("$json", t.ContentJson),
        ("$text", t.PlainText), ("$status", (int)t.Status), ("$created", t.CreatedAt), ("$updated", t.UpdatedAt),
        ("$completed", t.CompletedAt), ("$revision", t.Revision), ("$deleted", t.DeletedAt), ("$previous", t.PreviousStatus.HasValue ? (int)t.PreviousStatus.Value : null), ("$previousCompleted", t.PreviousCompletedAt)];
    public Task<TodoItem> UpdateContentAsync(string id, RichContent content, long revision, CancellationToken ct = default) => Run(c =>
    {
        ValidateContent(content); using var tx = c.BeginTransaction(); var old = Get(c, id);
        if (old.Status == TodoStatus.Deleted) throw new InvalidOperationException("请先恢复任务再编辑。");
        if (old.Revision != revision) throw new InvalidOperationException("任务已更新，请取消编辑后重新打开。");
        var now = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), old.UpdatedAt + 1);
        Database.Exec(c, "UPDATE Tasks SET ContentJson=$json,PlainText=$text,UpdatedAt=$now,Revision=Revision+1 WHERE Id=$id",
            ("$json", content.ToJson()), ("$text", content.PlainText), ("$now", now), ("$id", id));
        var item = Get(c, id); tx.Commit(); return item;
    }, ct);
    public Task<TodoItem> UpdateTaskAsync(string id, RichContent content, string? projectId, long revision, CancellationToken ct = default) => Run(c =>
    {
        ValidateContent(content); using var tx = c.BeginTransaction(); var old = Get(c, id);
        if (old.Status == TodoStatus.Deleted) throw new InvalidOperationException("请先恢复任务再编辑。");
        if (old.Revision != revision) throw new InvalidOperationException("任务已更新，请取消编辑后重新打开。");
        if (projectId != null && Convert.ToInt64(Database.Scalar(c, "SELECT COUNT(*) FROM Projects WHERE Id=$id", ("$id", projectId))) == 0)
            throw new ArgumentException("所选模块已不存在。");
        var now = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), old.UpdatedAt + 1);
        Database.Exec(c, "UPDATE Tasks SET ProjectId=$project,ContentJson=$json,PlainText=$text,UpdatedAt=$now,Revision=Revision+1 WHERE Id=$id",
            ("$project", projectId), ("$json", content.ToJson()), ("$text", content.PlainText), ("$now", now), ("$id", id));
        var item = Get(c, id); tx.Commit(); return item;
    }, ct);
    public Task<TodoItem> SetStatusAsync(string id, TodoStatus status, long revision, CancellationToken ct = default) => Run(c =>
    {
        if (!Enum.IsDefined(status) || status == TodoStatus.Deleted) throw new ArgumentException("未知任务状态。");
        using var tx = c.BeginTransaction(); var old = Get(c, id);
        if (old.Revision != revision) throw new InvalidOperationException("任务状态已更新，请重试。");
        if (old.Status == TodoStatus.Deleted) throw new InvalidOperationException("请使用恢复操作。");
        if (old.Status == status) return old;
        var now = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), old.UpdatedAt + 1);
        Database.Exec(c, "UPDATE Tasks SET Status=$status,CompletedAt=$completed,UpdatedAt=$now,Revision=Revision+1 WHERE Id=$id",
            ("$status", (int)status), ("$completed", status == TodoStatus.Completed ? now : null), ("$now", now), ("$id", id));
        Database.Exec(c, "INSERT INTO TaskStateEvents VALUES($event,$task,$from,$to,$now)",
            ("$event", Guid.NewGuid().ToString("N")), ("$task", id), ("$from", (int)old.Status), ("$to", (int)status), ("$now", now));
        var item = Get(c, id); tx.Commit(); return item;
    }, ct);
    public Task<TodoItem> DeleteTaskAsync(string id, long revision, CancellationToken ct = default) => DeleteOrRestoreAsync(id, revision, false, ct);
    public Task<TodoItem> RestoreTaskAsync(string id, long revision, CancellationToken ct = default) => DeleteOrRestoreAsync(id, revision, true, ct);
    private Task<TodoItem> DeleteOrRestoreAsync(string id, long revision, bool restore, CancellationToken ct) => Run(c =>
    {
        using var tx = c.BeginTransaction(); var old = Get(c, id);
        if (old.Revision != revision) throw new InvalidOperationException("任务状态已更新，请重试。");
        if (restore != (old.Status == TodoStatus.Deleted)) throw new InvalidOperationException("任务状态不支持此操作。");
        var now = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), old.UpdatedAt + 1);
        var status = restore ? old.PreviousStatus!.Value : TodoStatus.Deleted;
        Database.Exec(c, "UPDATE Tasks SET Status=$status,CompletedAt=$completed,DeletedAt=$deleted,PreviousStatus=$previous,PreviousCompletedAt=$previousCompleted,UpdatedAt=$now,Revision=Revision+1 WHERE Id=$id",
            ("$status", (int)status), ("$completed", restore ? old.PreviousCompletedAt : null),
            ("$deleted", restore ? null : now), ("$previous", restore ? null : (int)old.Status),
            ("$previousCompleted", restore ? null : old.CompletedAt), ("$now", now), ("$id", id));
        Database.Exec(c, "INSERT INTO TaskStateEvents VALUES($event,$task,$from,$to,$now)",
            ("$event", Guid.NewGuid().ToString("N")), ("$task", id), ("$from", (int)old.Status), ("$to", (int)status), ("$now", now));
        var item = Get(c, id); tx.Commit(); return item;
    }, ct);
    public Task<string?> GetSettingAsync(string key, CancellationToken ct = default) => Run(c => Database.Scalar(c,
        "SELECT Value FROM BookSettings WHERE Key=$key", ("$key", key)) as string, ct);
    public Task SetSettingAsync(string key, string value, CancellationToken ct = default) => Run(c => Database.Exec(c,
        "INSERT INTO BookSettings VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", ("$key", key), ("$value", value)), ct);
}
