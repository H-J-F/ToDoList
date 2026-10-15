using ToDoList.Core;
using ToDoList.Storage.Cloud;

namespace ToDoList.Storage;

public static class BookOperations
{
    public static Task<BookInfo> RenameAsync(BookLibrary library, BookInfo book, string title, CancellationToken ct = default) => Database.RunAsync(book.Path, () =>
    {
        title = BookRules.ValidateTitle(title);
        using var c = Database.Open(book.Path);
        var current = BookLibrary.ReadInfo(c, book.Path);
        if (current.BookId != book.BookId) throw new CloudLocalChangedException();
        if (current.Title != title)
        {
            Database.Exec(c, "UPDATE BookMetadata SET Title=$title WHERE Id=1", ("$title", title));
            Database.NotifyChanged(book.Path);
        }
        return current with { Title = title };
    }, ct);

    public static async Task<BookInfo> ImportAsync(BookLibrary library, PreparedImport prepared, CancellationToken ct = default)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "todo-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, prepared.Info.Name + ".db");
        try
        {
            Database.Snapshot(prepared.TemporaryPath, path);
            var incoming = await new CloudBookStorage(new(directory)).CaptureAsync(ct);
            var storage = new CloudBookStorage(library);
            var expected = await storage.CaptureAsync(ct);
            var baselinePath = System.IO.Path.Combine(storage.StateDirectory, "import-baselines.json");
            var baselines = File.Exists(baselinePath) ? CloudJson.Parse<Dictionary<string, CloudSnapshot>>(File.ReadAllText(baselinePath)) : [];
            var overlay = storage.ImportVersions();
            var local = CloudMerge.PrepareLocal(expected.Copy(), overlay, []);
            var merged = CloudMerge.Merge(baselines.GetValueOrDefault(prepared.Info.BookId) ?? new(), local, incoming);
            var order = expected.Values.Where(p => CloudSnapshot.Book(p.Key) == prepared.Info.BookId && CloudSnapshot.Kind(p.Key) == "project" && !p.Value[0].Deleted)
                .Select(p => p.Value[0].Read<ProjectInfo>().SortOrder).DefaultIfEmpty(-1).Max() + 1;
            foreach (var (key, versions) in incoming.Values.Where(p => CloudSnapshot.Kind(p.Key) == "project" && !p.Value[0].Deleted)
                .OrderBy(p => p.Value[0].Read<ProjectInfo>().SortOrder).ThenBy(p => p.Key))
                if (!expected.Values.ContainsKey(key)) merged.Values[key] = [CloudValue.From(versions[0].Read<ProjectInfo>() with { SortOrder = order++ })];
            merged.Validate();
            // Persist every unselected version before changing any database; retries remain lossless.
            storage.SaveImportVersions(new() { Values = new(merged.Conflicts.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal) });
            await storage.ApplyAsync(expected, merged, ct);
            baselines[prepared.Info.BookId] = incoming;
            CloudBookStorage.AtomicWrite(baselinePath, CloudJson.Serialize(baselines));
            return (await library.ListAsync()).Single(b => b.BookId == prepared.Info.BookId);
        }
        finally { Database.CleanupStaging(path); Directory.Delete(directory); }
    }

    public static async Task<CloudChangeSet> MigrateProjectAsync(BookLibrary library, BookInfo source, BookInfo target, string projectId,
        string? newName = null, string? mergeProjectId = null, IReadOnlyDictionary<string, List<CloudValue>>? conflicts = null,
        CancellationToken ct = default, Action<int>? beforeReplace = null)
    {
        if (source.BookId == target.BookId) throw new ArgumentException("请选择其他待办笔记。");
        var storage = new CloudBookStorage(library, beforeReplace);
        var expected = await storage.CaptureAsync(ct);
        var desired = expected.Copy();
        string Key(string book, string kind, string id) => CloudSnapshot.Key(book, kind, id);
        var projectKey = Key(source.BookId, "project", projectId);
        if (!expected.Values.TryGetValue(projectKey, out var pv) || pv[0].Deleted) throw new CloudLocalChangedException();
        if (!expected.Values.TryGetValue(Key(target.BookId, "book", target.BookId), out var bv) || bv[0].Deleted) throw new CloudLocalChangedException();
        var project = pv[0].Read<ProjectInfo>();
        var taskKeys = expected.Values.Where(p => CloudSnapshot.Book(p.Key) == source.BookId && CloudSnapshot.Kind(p.Key) == "task" &&
            !p.Value[0].Deleted && p.Value[0].Read<TodoItem>().ProjectId == projectId).Select(p => p.Key).ToHashSet();
        var taskIds = taskKeys.Select(CloudSnapshot.Id).ToHashSet();
        var eventKeys = expected.Values.Where(p => CloudSnapshot.Book(p.Key) == source.BookId && CloudSnapshot.Kind(p.Key) == "event" &&
            !p.Value[0].Deleted && taskIds.Contains(p.Value[0].Read<CloudStateEvent>().TaskId)).Select(p => p.Key).ToHashSet();
        var affected = taskKeys.Concat(eventKeys).Append(projectKey).ToHashSet();
        var allConflicts = (conflicts ?? new Dictionary<string, List<CloudValue>>()).Keys.Concat(storage.ImportVersions().Values.Keys);
        if (allConflicts.Any(k => affected.Contains(k) || CloudSnapshot.Book(k) == target.BookId || k == Key(source.BookId, "book", source.BookId)))
            throw new InvalidOperationException("请先处理相关笔记的同步冲突，再迁移模块。");
        var targetProjects = expected.Values.Where(p => CloudSnapshot.Book(p.Key) == target.BookId && CloudSnapshot.Kind(p.Key) == "project" && !p.Value[0].Deleted)
            .Select(p => p.Value[0].Read<ProjectInfo>()).ToArray();
        string targetProject;
        if (mergeProjectId != null)
        {
            if (!targetProjects.Any(p => p.Id == mergeProjectId)) throw new CloudLocalChangedException();
            targetProject = mergeProjectId;
        }
        else
        {
            var name = BookRules.ValidateTitle(newName ?? project.Name);
            if (name is "全部" or "新增" || targetProjects.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("目标笔记已有同名模块，请选择合并或使用其他名称。");
            if (targetProjects.Any(p => p.Id == projectId)) throw new InvalidOperationException("目标笔记已有相同 ID 的模块，请选择合并。");
            targetProject = projectId;
            desired.Set(target.BookId, "project", targetProject, project with { Name = name, SortOrder = targetProjects.Length == 0 ? 0 : targetProjects.Max(p => p.SortOrder) + 1 });
        }
        void Transfer(string sourceKey, CloudValue value)
        {
            var targetKey = Key(target.BookId, CloudSnapshot.Kind(sourceKey), CloudSnapshot.Id(sourceKey));
            if (desired.Values.TryGetValue(targetKey, out var existing) && existing.Any(v => !v.Deleted) && !CloudMerge.Equal(targetKey, existing, [value]))
                throw new InvalidOperationException("目标笔记存在相同 ID 但内容不同的记录，未迁移任何数据。");
            desired.Values[targetKey] = [value]; desired.Values[sourceKey] = [new(null)];
        }
        foreach (var key in taskKeys)
        {
            var task = expected.Values[key][0].Read<TodoItem>();
            // Compare logical content before assigning a new membership edit timestamp.
            var targetTaskKey = Key(target.BookId, "task", task.Id);
            var candidate = CloudValue.From(task with { ProjectId = targetProject });
            if (desired.Values.TryGetValue(targetTaskKey, out var existingTask) && existingTask.Any(v => !v.Deleted) && !CloudMerge.Equal(targetTaskKey, existingTask, [candidate]))
                throw new InvalidOperationException("目标笔记存在相同 ID 但内容不同的记录，未迁移任何数据。");
            if (task.ProjectId != targetProject)
            {
                var now = Math.Max(task.UpdatedAt + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                task = task with { ProjectId = targetProject, UpdatedAt = now, EditedAt = now, Revision = task.Revision + 1 };
            }
            Transfer(key, CloudValue.From(task));
        }
        foreach (var key in eventKeys) Transfer(key, expected.Values[key][0]);
        desired.Values[projectKey] = [new(null)];
        var remaining = desired.Values.Where(p => CloudSnapshot.Book(p.Key) == source.BookId && CloudSnapshot.Kind(p.Key) == "project" && !p.Value[0].Deleted)
            .OrderBy(p => p.Value[0].Read<ProjectInfo>().SortOrder).ThenBy(p => p.Key).ToArray();
        for (var i = 0; i < remaining.Length; i++) desired.Values[remaining[i].Key] = [CloudValue.From(remaining[i].Value[0].Read<ProjectInfo>() with { SortOrder = i })];
        desired.Validate();
        return await storage.ApplyWithChangesAsync(expected, desired, ct);
    }
}
