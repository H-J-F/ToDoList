using Microsoft.Data.Sqlite;
using ToDoList.Core;

namespace ToDoList.Storage.Cloud;

public sealed class CloudLocalChangedException() : IOException("本地数据在同步期间发生变化，将重新同步。");
public sealed class CloudBookStorage(BookLibrary library, Action<int>? beforeReplace = null)
{
    public BookLibrary Library { get; } = library;
    public string StateDirectory => System.IO.Path.Combine(Library.DataDirectory, ".cloud");
    private const string TaskColumns = "Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision,DeletedAt,PreviousStatus,PreviousCompletedAt,EditedAt";
    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void RecordBookDeletion(string directory, string bookPath)
    {
        using var c = Database.Open(bookPath, true);
        var id = (string?)Database.Scalar(c, "SELECT BookId FROM SyncIdentity WHERE Id=1") ?? throw new InvalidDataException("笔记缺少同步身份。");
        var path = System.IO.Path.Combine(directory, ".cloud", "deleted-books.json");
        var ids = File.Exists(path) ? CloudJson.Parse<HashSet<string>>(File.ReadAllText(path)) : [];
        ids.Add(id); AtomicWrite(path, CloudJson.Serialize(ids.Order(StringComparer.Ordinal).ToArray()));
    }
    public static void Recover(string directory)
    {
        var journalPath = System.IO.Path.Combine(directory, ".cloud", "apply-journal.json");
        if (!File.Exists(journalPath)) return;
        var entries = CloudJson.Parse<List<ApplyEntry>>(File.ReadAllText(journalPath));
        foreach (var entry in entries)
        {
            var target = SafeChild(directory, entry.Name + ".db");
            if (entry.Backup != null)
            {
                var backup = SafeChild(System.IO.Path.Combine(directory, "Backup"), entry.Backup);
                if (!File.Exists(backup)) throw new IOException("云同步恢复所需备份不存在，请保留现有数据。");
                var temp = target + ".recover-" + Guid.NewGuid().ToString("N");
                try { Database.Snapshot(backup, temp); Replace(temp, target); }
                finally { Database.CleanupStaging(temp); }
            }
            else if (File.Exists(target))
            {
                // Only remove a file that this journal created, after checking its identity.
                using (var c = Database.Open(target, true))
                    if ((string?)Database.Scalar(c, "SELECT BookId FROM SyncIdentity WHERE Id=1") != entry.BookId)
                        throw new IOException("云同步恢复发现同名的其他笔记，未修改该文件。");
                Database.CleanupStaging(target);
            }
        }
        File.Delete(journalPath);
    }
    private static string SafeChild(string directory, string name)
    {
        if (name != System.IO.Path.GetFileName(name) || name is "." or "..") throw new InvalidDataException("云同步文件路径无效。");
        return System.IO.Path.Combine(System.IO.Path.GetFullPath(directory), name);
    }
    private static void Replace(string temp, string path)
    {
        if (File.Exists(path))
        {
            using (var c = Database.Open(path)) Database.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            File.Replace(temp, path, null);
        }
        else File.Move(temp, path);
    }
    private CloudSnapshot CaptureLocked()
    {
        var snapshot = new CloudSnapshot(); var presentBooks = new HashSet<string>();
        foreach (var path in Directory.EnumerateFiles(Library.DataDirectory, "*.db", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            using var c = Database.Open(path, true);
            var book = BookLibrary.Validate(c, path);
            var id = (string?)Database.Scalar(c, "SELECT BookId FROM SyncIdentity WHERE Id=1") ?? throw new InvalidDataException("笔记缺少同步身份。");
            if (!Guid.TryParseExact(id, "N", out _) || !presentBooks.Add(id)) throw new InvalidDataException("存在重复或无效的笔记同步身份，请另存副本后重试。");
            var logicalName = (string?)Database.Scalar(c, "SELECT LogicalName FROM SyncIdentity WHERE Id=1") ?? book.Name;
            snapshot.Set(id, "book", id, new CloudBookMeta(logicalName, book.Title));
            using (var cmd = Database.Command(c, "SELECT Id,Name,SortOrder FROM Projects"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) snapshot.Set(id, "project", r.GetString(0), new ProjectInfo(r.GetString(0), r.GetString(1), r.GetInt32(2)));
            using (var cmd = Database.Command(c, "SELECT " + TaskColumns + " FROM Tasks"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) { var task = TaskRepository.Read(r); snapshot.Set(id, "task", task.Id, task); }
            using (var cmd = Database.Command(c, "SELECT Id,TaskId,FromStatus,ToStatus,OccurredAt FROM TaskStateEvents"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) snapshot.Set(id, "event", r.GetString(0), new CloudStateEvent(r.GetString(0), r.GetString(1), (TodoStatus)r.GetInt32(2), (TodoStatus)r.GetInt32(3), r.GetInt64(4)));
            using (var cmd = Database.Command(c, "SELECT Key,Value FROM BookSettings"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) snapshot.Set(id, "setting", r.GetString(0), r.GetString(1));
            using (var cmd = Database.Command(c, "SELECT Kind,EntityId FROM SyncTombstones"))
            using (var r = cmd.ExecuteReader()) while (r.Read())
                if (!snapshot.Values.ContainsKey(CloudSnapshot.Key(id, r.GetString(0), r.GetString(1)))) snapshot.Delete(id, r.GetString(0), r.GetString(1));
        }
        var deletedPath = System.IO.Path.Combine(StateDirectory, "deleted-books.json");
        if (File.Exists(deletedPath)) foreach (var id in CloudJson.Parse<List<string>>(File.ReadAllText(deletedPath)))
            if (!presentBooks.Contains(id)) snapshot.Delete(id, "book", id);
        snapshot.Validate(); return snapshot;
    }
    public async Task<CloudSnapshot> CaptureAsync(CancellationToken ct = default)
    {
        await Library.ListAsync();
        if (Library.ScanWarning != null) throw new InvalidDataException("存在未能读取的笔记，已暂停同步：" + Library.ScanWarning);
        return await Database.RunDirectoryAsync(Library.DataDirectory, CaptureLocked, ct);
    }
    public Task<CloudChangeSet> ApplyWithChangesAsync(CloudSnapshot expected, CloudSnapshot desired, CancellationToken ct = default) => Database.RunDirectoryAsync(Library.DataDirectory, () =>
    {
        ct.ThrowIfCancellationRequested(); desired.Validate(); Recover(Library.DataDirectory);
        var actual = CaptureLocked();
        if (CloudJson.Serialize(actual) != CloudJson.Serialize(expected)) throw new CloudLocalChangedException();
        var selected = Select(actual, desired);
        var changes = Changes(actual, selected);
        if (!changes.HasChanges) return changes;
        var existingPaths = new Dictionary<string, string>();
        foreach (var path in Directory.EnumerateFiles(Library.DataDirectory, "*.db"))
        {
            using var c = Database.Open(path, true); existingPaths.Add((string)Database.Scalar(c, "SELECT BookId FROM SyncIdentity WHERE Id=1")!, path);
        }
        var usedNames = existingPaths.Values.Select(System.IO.Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staging = System.IO.Path.Combine(StateDirectory, "staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        var entries = new List<ApplyEntry>(); var stages = new Dictionary<string, string>();
        try
        {
            foreach (var (key, versions) in selected.Values.Where(p => CloudSnapshot.Kind(p.Key) == "book"))
            {
                ct.ThrowIfCancellationRequested(); var id = CloudSnapshot.Book(key); var value = versions[0];
                existingPaths.TryGetValue(id, out var existing);
                var oldValues = new CloudSnapshot { Values = new(actual.Values.Where(p => CloudSnapshot.Book(p.Key) == id).ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal) };
                var newValues = new CloudSnapshot { Values = new(selected.Values.Where(p => CloudSnapshot.Book(p.Key) == id).ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal) };
                if (existing != null && CloudJson.Serialize(oldValues) == CloudJson.Serialize(newValues)) continue;
                if (value.Deleted && existing == null) continue;
                var meta = value.Deleted ? null : value.Read<CloudBookMeta>();
                var name = existing != null ? System.IO.Path.GetFileNameWithoutExtension(existing) : meta!.Name;
                if (existing == null && !usedNames.Add(name)) { name = meta!.Name[..Math.Min(48, meta.Name.Length)] + id[..12]; if (!usedNames.Add(name)) throw new IOException("同步笔记文件名重复。"); }
                var backupName = existing == null ? null : name + "-cloud-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".db";
                if (existing != null) { Directory.CreateDirectory(Library.BackupDirectory); Database.Snapshot(existing, SafeChild(Library.BackupDirectory, backupName!)); }
                entries.Add(new(id, name, backupName, value.Deleted));
                if (value.Deleted) continue;
                var temp = SafeChild(staging, id + ".db");
                var title = meta!.Title;
                Database.Create(temp, name, title);
                using (var c = Database.Open(temp))
                {
                    using var tx = c.BeginTransaction();
                    Database.Exec(c, "UPDATE SyncIdentity SET BookId=$id,LogicalName=$name WHERE Id=1", ("$id", id), ("$name", meta.Name));
                    var projectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (_, pv) in newValues.Values.Where(p => CloudSnapshot.Kind(p.Key) == "project" && !p.Value[0].Deleted))
                    {
                        var project = pv[0].Read<ProjectInfo>(); var projectName = project.Name;
                        if (!projectNames.Add(projectName)) { projectName = projectName[..Math.Min(80, projectName.Length)] + "（副本 " + project.Id![..8] + "）"; if (!projectNames.Add(projectName)) throw new InvalidDataException("云端模块名称冲突。"); }
                        Database.Exec(c, "INSERT INTO Projects VALUES($id,$name,$order)", ("$id", project.Id), ("$name", projectName), ("$order", project.SortOrder));
                    }
                    var tasks = new HashSet<string>();
                    foreach (var (taskKey, tv) in newValues.Values.Where(p => CloudSnapshot.Kind(p.Key) == "task" && !p.Value[0].Deleted))
                    {
                        var task = tv[0].Read<TodoItem>(); tasks.Add(task.Id);
                        if (actual.Values.TryGetValue(taskKey, out var old) && !old[0].Deleted && old[0].Semantic(taskKey) != tv[0].Semantic(taskKey)) task = task with { Revision = Math.Max(task.Revision, old[0].Read<TodoItem>().Revision) + 1 };
                        Database.Exec(c, "INSERT INTO Tasks(" + TaskColumns + ") VALUES($id,$project,$json,$text,$status,$created,$updated,$completed,$revision,$deleted,$previous,$previousCompleted,$edited)", TaskRepository.Args(task));
                    }
                    foreach (var (_, evv) in newValues.Values.Where(p => CloudSnapshot.Kind(p.Key) == "event" && !p.Value[0].Deleted))
                    {
                        var ev = evv[0].Read<CloudStateEvent>(); if (!tasks.Contains(ev.TaskId)) continue;
                        Database.Exec(c, "INSERT INTO TaskStateEvents VALUES($id,$task,$from,$to,$at)", ("$id", ev.Id), ("$task", ev.TaskId), ("$from", (int)ev.FromStatus), ("$to", (int)ev.ToStatus), ("$at", ev.OccurredAt));
                    }
                    foreach (var (settingKey, sv) in newValues.Values.Where(p => CloudSnapshot.Kind(p.Key) == "setting" && !p.Value[0].Deleted))
                        Database.Exec(c, "INSERT INTO BookSettings VALUES($key,$value)", ("$key", CloudSnapshot.Id(settingKey)), ("$value", sv[0].Read<string>()));
                    foreach (var (deletedKey, dv) in newValues.Values.Where(p => p.Value[0].Deleted && CloudSnapshot.Kind(p.Key) is "task" or "project" or "event"))
                        Database.Exec(c, "INSERT INTO SyncTombstones VALUES($kind,$id)", ("$kind", CloudSnapshot.Kind(deletedKey)), ("$id", CloudSnapshot.Id(deletedKey)));
                    tx.Commit(); BookLibrary.Validate(c, temp);
                }
                // Close WAL before an atomic file replacement; keep a standalone snapshot.
                var standalone = temp + ".snapshot"; Database.Snapshot(temp, standalone); Database.CleanupStaging(temp); File.Move(standalone, temp);
                stages.Add(id, temp);
            }
            var journalPath = System.IO.Path.Combine(StateDirectory, "apply-journal.json");
            AtomicWrite(journalPath, CloudJson.Serialize(entries));
            var replaceIndex = 0;
            foreach (var entry in entries)
            {
                // Once the journal is durable, finish or roll back as a unit, even if cancelled.
                beforeReplace?.Invoke(replaceIndex++);
                var target = SafeChild(Library.DataDirectory, entry.Name + ".db");
                if (entry.Deleted) { RecordBookDeletion(Library.DataDirectory, target); Database.CleanupStaging(target); }
                else Replace(stages[entry.BookId], target);
            }
            File.Delete(journalPath);
            Database.NotifyChanged(System.IO.Path.Combine(Library.DataDirectory, ".cloud-applied")); return changes;
        }
        catch { Recover(Library.DataDirectory); throw; }
        finally
        {
            // Staging is an exact, newly-created directory, never a user data path.
            foreach (var path in Directory.EnumerateFiles(staging)) File.Delete(path);
            Directory.Delete(staging);
        }
    }, ct);
    public async Task<bool> ApplyAsync(CloudSnapshot expected, CloudSnapshot desired, CancellationToken ct = default) =>
        (await ApplyWithChangesAsync(expected, desired, ct)).HasChanges;
    public static CloudSnapshot Select(CloudSnapshot actual, CloudSnapshot desired)
    {
        var selected = new CloudSnapshot();
        foreach (var (key, versions) in desired.Values)
        {
            var existing = actual.Values.GetValueOrDefault(key)?.FirstOrDefault();
            var version = existing == null ? null : versions.FirstOrDefault(v => v.Semantic(key) == existing.Semantic(key));
            selected.Values[key] = [version != null ? existing! : versions.FirstOrDefault(v => !v.Deleted) ?? versions[0]];
        }
        // A conflicted deletion cannot remove the container of a retained task.
        foreach (var (key, versions) in selected.Values.Where(p => CloudSnapshot.Kind(p.Key) == "task" && !p.Value[0].Deleted).ToArray())
        {
            var task = versions[0].Read<TodoItem>(); var book = CloudSnapshot.Book(key);
            foreach (var parentKey in new[] { CloudSnapshot.Key(book, "book", book), task.ProjectId == null ? null : CloudSnapshot.Key(book, "project", task.ProjectId) }.OfType<string>())
                if (!selected.Values.TryGetValue(parentKey, out var parent) || parent[0].Deleted)
                {
                    var retained = desired.Values.GetValueOrDefault(parentKey)?.FirstOrDefault(v => !v.Deleted);
                    if (retained == null) throw new InvalidDataException("同步后的笔记关联不完整。");
                    selected.Values[parentKey] = [retained];
                }
        }
        selected.Validate(); return selected;
    }
    public static CloudChangeSet PreviewChanges(CloudSnapshot actual, CloudSnapshot desired) => Changes(actual, Select(actual, desired));
    private static CloudChangeSet Changes(CloudSnapshot actual, CloudSnapshot selected) => new(actual.Values.Keys.Concat(selected.Values.Keys).Distinct()
        .Where(key => !CloudMerge.Equal(key, actual.Values.GetValueOrDefault(key), selected.Values.GetValueOrDefault(key))).ToHashSet());
    public CloudSnapshot ImportVersions()
    {
        var path = System.IO.Path.Combine(StateDirectory, "import-versions.json");
        var versions = File.Exists(path) ? CloudJson.Parse<CloudSnapshot>(File.ReadAllText(path)) : new();
        // This is a partial overlay, not a standalone snapshot; dependencies live in the databases.
        return versions;
    }
    public void SaveImportVersions(CloudSnapshot versions) => AtomicWrite(System.IO.Path.Combine(StateDirectory, "import-versions.json"), CloudJson.Serialize(versions));
    private sealed record ApplyEntry(string BookId, string Name, string? Backup, bool Deleted);
}

public sealed record CloudChangeSet(IReadOnlySet<string> Keys)
{
    public bool HasChanges => Keys.Count > 0;
    public bool Affects(string bookId) => Keys.Any(k => CloudSnapshot.Book(k) == bookId);
    public bool HasKind(string bookId, string kind) => Keys.Any(k => CloudSnapshot.Book(k) == bookId && CloudSnapshot.Kind(k) == kind);
}
