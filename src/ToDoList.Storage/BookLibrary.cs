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
    public Task<IReadOnlyList<BookInfo>> ListAsync() => Database.RunDirectoryAsync<IReadOnlyList<BookInfo>>(DataDirectory, () =>
    {
        EnsureWritable(); Cloud.CloudBookStorage.Recover(DataDirectory); var books = new List<BookInfo>(); var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(DataDirectory, "*.db", SearchOption.TopDirectoryOnly))
        {
            try
            {
                Database.Upgrade(path, BackupDirectory);
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
            Database.NotifyChanged(path);
            using var created = Database.Open(path, true);
            return ReadInfo(created, path);
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
        var id = (string?)Database.Scalar(c, "SELECT BookId FROM SyncIdentity WHERE Id=1");
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("笔记身份无效。");
        var hidden = string.Equals(Database.Scalar(c, "SELECT Value FROM BookSettings WHERE Key=$key", ("$key", BookInfo.HiddenSetting)) as string, "true", StringComparison.OrdinalIgnoreCase);
        return new(name, title, path, id!, hidden);
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
        using (var cmd = Database.Command(c, "SELECT Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision,DeletedAt,PreviousStatus,PreviousCompletedAt,EditedAt FROM Tasks"))
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
            if (item.EditedAt is long edited)
            {
                _ = DateTimeOffset.FromUnixTimeMilliseconds(edited);
                if (edited < item.CreatedAt || edited > item.UpdatedAt) throw new InvalidDataException("任务修改时间无效。");
            }
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
    // Identity-preserving import; the source database is never changed.
    public async Task<BookInfo> CopyBookAsync(BookLibrary source, BookInfo book, CancellationToken ct = default)
    {
        var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ToDoList-cloud-copy-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await source.ExportAsync(book, temporary);
            using var prepared = await PrepareImportAsync(temporary, ct);
            return await BookOperations.ImportAsync(this, prepared, ct);
        }
        finally { Database.CleanupStaging(temporary); }
    }
    public Task<BookInfo> ImportAsync(PreparedImport prepared, BookInfo? existing, ImportMode mode, CancellationToken ct = default)
    {
        if (mode == ImportMode.Merge || existing == null) return BookOperations.ImportAsync(this, prepared, ct);
        return Database.RunAsync(existing.Path, () =>
        {
            var path = existing.Path;
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".staging";
            try
            {
                Directory.CreateDirectory(BackupDirectory);
                var backup = System.IO.Path.Combine(BackupDirectory, $"{existing.Name}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
                Database.Snapshot(path, backup); Database.Snapshot(prepared.TemporaryPath, temp);
                using (var original = Database.Open(path, true))
                using (var staged = Database.Open(temp))
                {
                    if (ReadInfo(original, path).BookId != existing.BookId) throw new Cloud.CloudLocalChangedException();
                    Database.Exec(staged, "UPDATE BookMetadata SET Name=$name WHERE Id=1", ("$name", existing.Name));
                    Database.Exec(staged, "UPDATE SyncIdentity SET BookId=$id,LogicalName=$name WHERE Id=1",
                        ("$id", existing.BookId), ("$name", (string)Database.Scalar(original, "SELECT LogicalName FROM SyncIdentity WHERE Id=1")!));
                    foreach (var (kind, table) in new[] { ("task", "Tasks"), ("project", "Projects"), ("event", "TaskStateEvents") })
                    {
                        using var cmd = Database.Command(original, "SELECT Id FROM " + table); using var r = cmd.ExecuteReader();
                        while (r.Read()) Database.Exec(staged, "INSERT OR IGNORE INTO SyncTombstones SELECT $kind,$id WHERE NOT EXISTS(SELECT 1 FROM " + table + " WHERE Id=$id)", ("$kind", kind), ("$id", r.GetString(0)));
                    }
                    using (var cmd = Database.Command(original, "SELECT Kind,EntityId FROM SyncTombstones"))
                    using (var r = cmd.ExecuteReader()) while (r.Read()) Database.Exec(staged, "INSERT OR IGNORE INTO SyncTombstones VALUES($kind,$id)", ("$kind", r.GetString(0)), ("$id", r.GetString(1)));
                    Validate(staged, temp, ct);
                }
                ct.ThrowIfCancellationRequested();
                // Produce a standalone file before replacement so committed staging WAL cannot be lost.
                var standalone = temp + ".snapshot"; Database.Snapshot(temp, standalone); Database.CleanupStaging(temp); File.Move(standalone, temp);
                using (var c = Database.Open(path)) Database.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
                File.Replace(temp, path, null);
                using var result = Database.Open(path); Database.Exec(result, "PRAGMA journal_mode=WAL;");
                Database.NotifyChanged(path); return ReadInfo(result, path);
            }
            finally { Database.CleanupStaging(temp); }
        }, ct);
    }
    public Task<string> DeleteAsync(BookInfo book, CancellationToken ct = default) => Database.RunAsync(book.Path, () =>
    {
        EnsureWritable(); Directory.CreateDirectory(BackupDirectory);
        var backup = System.IO.Path.Combine(BackupDirectory, $"{book.Name}-deleted-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        Database.Snapshot(book.Path, backup); ct.ThrowIfCancellationRequested();
        Cloud.CloudBookStorage.RecordBookDeletion(DataDirectory, book.Path);
        using (var c = Database.Open(book.Path)) Database.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        foreach (var path in new[] { book.Path, book.Path + "-wal", book.Path + "-shm" }) if (File.Exists(path)) File.Delete(path);
        Database.NotifyChanged(book.Path); return backup;
    }, ct);
    public AppSettings LoadSettings()
    {
        var path = System.IO.Path.Combine(DataDirectory, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
            ThemeCatalog.Normalize(settings);
            settings.LastBooksByWorkspace ??= [];
            settings.CloudSyncIntervalMinutes = CloudSyncSchedule.Normalize(settings.CloudSyncIntervalMinutes);
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
