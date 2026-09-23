using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToDoList.Core;

namespace ToDoList.Storage;

public sealed class PreparedImport(BookInfo info, string temporaryPath) : IDisposable
{
    public BookInfo Info { get; } = info;
    public string TemporaryPath { get; } = temporaryPath;
    public void Dispose() => Database.CleanupStaging(TemporaryPath);
}

public sealed class BookLibrary
{
    public string DataDirectory { get; }
    public string BackupDirectory => System.IO.Path.Combine(DataDirectory, "Backup");
    public string? ScanWarning { get; private set; }
    public BookLibrary(string dataDirectory) => DataDirectory = System.IO.Path.GetFullPath(dataDirectory);
    public void EnsureWritable()
    {
        Directory.CreateDirectory(DataDirectory);
        var probe = System.IO.Path.Combine(DataDirectory, ".write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
    }
    public Task<IReadOnlyList<BookInfo>> ListAsync() => Task.Run<IReadOnlyList<BookInfo>>(() =>
    {
        EnsureWritable(); var books = new List<BookInfo>(); var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(DataDirectory, "*.db", SearchOption.TopDirectoryOnly))
        {
            try
            {
                Database.RunAsync(path, () => { Database.Upgrade(path, BackupDirectory); return true; }).GetAwaiter().GetResult();
                using var c = Database.Open(path, true); var info = ReadInfo(c, path);
                if (!System.IO.Path.GetFileNameWithoutExtension(path).Equals(info.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("文件名与书内标识不一致，请通过导入添加。");
                books.Add(info);
            }
            catch (Exception ex) { errors.Add($"{System.IO.Path.GetFileName(path)}：{ex.Message}"); }
        }
        ScanWarning = errors.Count == 0 ? null : string.Join("\n", errors);
        return books.OrderBy(b => b.Title, StringComparer.CurrentCulture).ToList();
    });
    public Task<BookInfo> CreateAsync(string name, string title)
    {
        BookRules.ValidateName(name); title = BookRules.ValidateTitle(title);
        var path = System.IO.Path.Combine(DataDirectory, name + ".db");
        return Database.RunAsync(path, () =>
        {
            EnsureWritable();
            if (Directory.EnumerateFiles(DataDirectory, "*.db").Any(p => System.IO.Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已有相同标识名的待办笔记。");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".new";
            try { Database.Create(temp, name, title); File.Move(temp, path); }
            finally { Database.CleanupStaging(temp); }
            return new BookInfo(name, title, path);
        });
    }
    public static BookInfo ReadInfo(SqliteConnection c, string path)
    {
        if (Convert.ToInt32(Database.Scalar(c, "PRAGMA application_id")) != Database.ApplicationId)
            throw new InvalidDataException("这不是 ToDoList 待办笔记。");
        var version = Convert.ToInt32(Database.Scalar(c, "PRAGMA user_version"));
        if (version != Database.Version) throw new InvalidDataException($"不支持待办笔记格式版本 {version}，请使用匹配的软件版本。");
        using var cmd = Database.Command(c, "SELECT Name,Title FROM BookMetadata WHERE Id=1");
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidDataException("待办笔记缺少名称。");
        var name = r.GetString(0); var title = r.GetString(1);
        BookRules.ValidateName(name); BookRules.ValidateTitle(title);
        return new(name, title, path);
    }
    public Task ExportAsync(BookInfo book, string destination) => Database.RunAsync(book.Path, () =>
    {
        destination = System.IO.Path.GetFullPath(destination);
        if (destination.Equals(book.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能导出覆盖正在使用的待办笔记。");
        var parent = System.IO.Path.GetDirectoryName(destination)!;
        // Do not overwrite any managed book through the export dialog.
        if (parent.Equals(DataDirectory, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请导出到 Data 之外的目录。");
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".export";
        try
        {
            Database.Snapshot(book.Path, temp);
            if (File.Exists(destination)) File.Replace(temp, destination, null); else File.Move(temp, destination);
        }
        finally { Database.CleanupStaging(temp); }
        return true;
    });
    public Task<PreparedImport> PrepareImportAsync(string source, CancellationToken ct = default) => Task.Run(() =>
    {
        EnsureWritable(); var temp = System.IO.Path.Combine(DataDirectory, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Database.Snapshot(source, temp);
            Database.Upgrade(temp, null);
            using var c = Database.Open(temp, true);
            var info = Validate(c, temp, ct);
            return new PreparedImport(info, temp);
        }
        catch { Database.CleanupStaging(temp); throw; }
    }, ct);
    public static BookInfo Validate(SqliteConnection c, string path, CancellationToken ct = default)
    {
        if (!Equals(Database.Scalar(c, "PRAGMA quick_check"), "ok")) throw new InvalidDataException("数据库完整性检查失败。");
        if (Convert.ToInt64(Database.Scalar(c, "SELECT COUNT(*) FROM sqlite_schema WHERE type IN ('view','trigger')")) != 0)
            throw new InvalidDataException("待办笔记包含不支持的数据库对象。");
        var info = ReadInfo(c, path);
        using (var cmd = Database.Command(c, "PRAGMA foreign_key_check"))
        using (var r = cmd.ExecuteReader()) if (r.Read()) throw new InvalidDataException("任务关联不完整。");
        using (var cmd = Database.Command(c, "SELECT Id,Name,SortOrder FROM Projects"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(r.GetString(0), out _)) throw new InvalidDataException("模块标识无效。");
            BookRules.ValidateTitle(r.GetString(1));
            if (r.GetInt32(2) < 0) throw new InvalidDataException("模块顺序无效。");
        }
        using (var cmd = Database.Command(c, "SELECT Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision,DeletedAt,PreviousStatus,PreviousCompletedAt FROM Tasks"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            ct.ThrowIfCancellationRequested(); var item = TaskRepository.Read(r);
            if (!Guid.TryParse(item.Id, out _) || !Enum.IsDefined(item.Status) || item.Revision < 1 ||
                (item.Status == TodoStatus.Completed) != item.CompletedAt.HasValue)
                throw new InvalidDataException("任务状态或标识无效。");
            if (item.Status == TodoStatus.Deleted)
            {
                if (!item.DeletedAt.HasValue || item.PreviousStatus is not (TodoStatus.Open or TodoStatus.Verification or TodoStatus.Completed)
                    || (item.PreviousStatus == TodoStatus.Completed) != item.PreviousCompletedAt.HasValue)
                    throw new InvalidDataException("删除恢复信息无效。");
                _ = DateTimeOffset.FromUnixTimeMilliseconds(item.DeletedAt.Value);
                if (item.PreviousCompletedAt.HasValue) _ = DateTimeOffset.FromUnixTimeMilliseconds(item.PreviousCompletedAt.Value);
            }
            else if (item.DeletedAt.HasValue || item.PreviousStatus.HasValue || item.PreviousCompletedAt.HasValue)
                throw new InvalidDataException("非删除任务包含删除恢复信息。");
            var content = RichContent.Parse(item.ContentJson);
            if (content.PlainText != item.PlainText || string.IsNullOrWhiteSpace(item.PlainText)) throw new InvalidDataException("任务内容不一致。");
            _ = DateTimeOffset.FromUnixTimeMilliseconds(item.CreatedAt); _ = DateTimeOffset.FromUnixTimeMilliseconds(item.UpdatedAt);
            if (item.CompletedAt.HasValue) _ = DateTimeOffset.FromUnixTimeMilliseconds(item.CompletedAt.Value);
        }
        using (var cmd = Database.Command(c, "SELECT Id,FromStatus,ToStatus,OccurredAt FROM TaskStateEvents"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(r.GetString(0), out _) || r.GetInt32(1) is < 0 or > 3 || r.GetInt32(2) is < 0 or > 3)
                throw new InvalidDataException("状态历史无效。");
            _ = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3));
        }
        return info;
    }
    public Task<BookInfo> ImportAsync(PreparedImport prepared, BookInfo? existing, ImportMode mode, CancellationToken ct = default)
    {
        var path = existing?.Path ?? System.IO.Path.Combine(DataDirectory, prepared.Info.Name + ".db");
        return Database.RunAsync(path, () =>
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".staging";
            try
            {
                if (existing == null)
                {
                    if (File.Exists(path)) throw new IOException("同名待办笔记已存在，请重新导入。");
                    Database.Snapshot(prepared.TemporaryPath, temp); File.Move(temp, path);
                }
                else
                {
                    Directory.CreateDirectory(BackupDirectory);
                    var backup = System.IO.Path.Combine(BackupDirectory, $"{existing.Name}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
                    Database.Snapshot(path, backup);
                    Database.Snapshot(mode == ImportMode.Merge ? path : prepared.TemporaryPath, temp);
                    if (mode == ImportMode.Merge) Merge(temp, prepared.TemporaryPath, ct);
                    using (var c = Database.Open(temp, true)) Validate(c, temp, ct);
                    ct.ThrowIfCancellationRequested();
                    using (var c = Database.Open(path)) Database.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
                    File.Replace(temp, path, null);
                }
                using var result = Database.Open(path); Database.Exec(result, "PRAGMA journal_mode=WAL;");
                return ReadInfo(result, path);
            }
            finally { Database.CleanupStaging(temp); }
        }, ct);
    }
    private static void Merge(string destination, string source, CancellationToken ct)
    {
        using var to = Database.Open(destination); using var from = Database.Open(source, true); using var tx = to.BeginTransaction();
        var map = new Dictionary<string, string>();
        using (var cmd = Database.Command(from, "SELECT Id,Name,SortOrder FROM Projects ORDER BY SortOrder,Name,Id"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            ct.ThrowIfCancellationRequested(); var id = r.GetString(0); var name = r.GetString(1);
            var local = Database.Scalar(to, "SELECT Id FROM Projects WHERE Id=$id OR Name=$name COLLATE NOCASE ORDER BY CASE WHEN Id=$id THEN 0 ELSE 1 END LIMIT 1", ("$id", id), ("$name", name)) as string;
            var order = Convert.ToInt32(Database.Scalar(to, "SELECT COALESCE(MAX(SortOrder),-1)+1 FROM Projects"));
            if (local == null) { Database.Exec(to, "INSERT INTO Projects(Id,Name,SortOrder) VALUES($id,$name,$order)", ("$id", id), ("$name", name), ("$order", order)); local = id; }
            map[id] = local;
        }
        using (var cmd = Database.Command(from, "SELECT Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision,DeletedAt,PreviousStatus,PreviousCompletedAt FROM Tasks"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            ct.ThrowIfCancellationRequested(); var item = TaskRepository.Read(r);
            if (item.ProjectId != null) item = item with { ProjectId = map[item.ProjectId] };
            Database.Exec(to, """
                INSERT INTO Tasks(Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision,DeletedAt,PreviousStatus,PreviousCompletedAt)
                VALUES($id,$project,$json,$text,$status,$created,$updated,$completed,$revision,$deleted,$previous,$previousCompleted)
                ON CONFLICT(Id) DO UPDATE SET ProjectId=excluded.ProjectId, ContentJson=excluded.ContentJson,
                PlainText=excluded.PlainText, Status=excluded.Status, CreatedAt=excluded.CreatedAt,
                UpdatedAt=excluded.UpdatedAt, CompletedAt=excluded.CompletedAt, DeletedAt=excluded.DeletedAt, PreviousStatus=excluded.PreviousStatus, PreviousCompletedAt=excluded.PreviousCompletedAt, Revision=MAX(Tasks.Revision,excluded.Revision)+1
                WHERE excluded.UpdatedAt>Tasks.UpdatedAt
                """, TaskRepository.Args(item));
        }
        using (var cmd = Database.Command(from, "SELECT Id,TaskId,FromStatus,ToStatus,OccurredAt FROM TaskStateEvents"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            ct.ThrowIfCancellationRequested(); Database.Exec(to, "INSERT OR IGNORE INTO TaskStateEvents VALUES($id,$task,$from,$to,$at)",
                ("$id", r.GetString(0)), ("$task", r.GetString(1)), ("$from", r.GetInt32(2)), ("$to", r.GetInt32(3)), ("$at", r.GetInt64(4)));
        }
        foreach (var key in new[] { "StatusColor.Open", "StatusColor.Verification", "StatusColor.Completed", "CompletedSort" })
        {
            var value = Database.Scalar(from, "SELECT Value FROM BookSettings WHERE Key=$key", ("$key", key)) as string;
            if (value != null) Database.Exec(to, "INSERT INTO BookSettings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", ("$key", key), ("$value", value));
        }
        tx.Commit(); Database.Exec(to, "PRAGMA optimize;");
    }
    public Task<string> DeleteAsync(BookInfo book, CancellationToken ct = default) => Database.RunAsync(book.Path, () =>
    {
        EnsureWritable(); Directory.CreateDirectory(BackupDirectory);
        var backup = System.IO.Path.Combine(BackupDirectory, $"{book.Name}-deleted-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        Database.Snapshot(book.Path, backup); ct.ThrowIfCancellationRequested();
        using (var c = Database.Open(book.Path)) Database.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        foreach (var path in new[] { book.Path, book.Path + "-wal", book.Path + "-shm" }) if (File.Exists(path)) File.Delete(path);
        return backup;
    }, ct);
    public AppSettings LoadSettings()
    {
        var path = System.IO.Path.Combine(DataDirectory, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
            settings.RecentColors = (settings.RecentColors ?? []).Where(c => c != null && System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$")).Select(c => c.ToUpperInvariant()).Distinct().Take(8).ToList();
            settings.RecentEmoji = (settings.RecentEmoji ?? []).Where(e => !string.IsNullOrWhiteSpace(e) && e.Length <= 64).Distinct().Take(24).ToList();
            return settings;
        }
        catch (JsonException) { return new(); }
    }
    public void SaveSettings(AppSettings settings)
    {
        EnsureWritable(); var path = System.IO.Path.Combine(DataDirectory, "settings.json"); var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
