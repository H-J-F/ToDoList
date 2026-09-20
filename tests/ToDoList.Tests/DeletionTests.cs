using Microsoft.Data.Sqlite;
using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.Tests;

public sealed class DeletionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "todo-deletion-" + Guid.NewGuid().ToString("N"));
    private BookLibrary Library => new(Path.Combine(root, "Data"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Theory][InlineData("Book1")][InlineData("123")][InlineData("aB09")]
    public void LettersAndDigitsAreValid(string name) => BookRules.ValidateName(name);

    [Theory][InlineData(TodoStatus.Open)][InlineData(TodoStatus.Verification)][InlineData(TodoStatus.Completed)]
    public async Task DeletionRestoresExactStateAndContent(TodoStatus status)
    {
        var book = await Library.CreateAsync("Book1", "测试"); var repo = new TaskRepository(book.Path);
        var content = new RichContent(1, [new([new("保留格式 🎯", true, true, true, "#123456", "https://example.com")])]);
        var original = await repo.AddTaskAsync(content, null, "项目");
        original = await repo.SetStatusAsync(original.Id, status, original.Revision);
        var deleted = await repo.DeleteTaskAsync(original.Id, original.Revision);
        Assert.Equal(TodoStatus.Deleted, deleted.Status); Assert.Null(deleted.CompletedAt);
        Assert.Equal(original.Status, deleted.PreviousStatus); Assert.Equal(original.CompletedAt, deleted.PreviousCompletedAt);
        Assert.NotNull(deleted.DeletedAt); Assert.Equal(original.ContentJson, deleted.ContentJson);
        foreach (var filter in new[] { TaskFilter.Open, TaskFilter.Completed })
        {
            var q = new TaskQuery(null, filter, TaskSort.Created);
            Assert.False(q.Matches(deleted)); Assert.Empty((await repo.QueryAsync(q)).Items);
        }
        Assert.Single((await repo.QueryAsync(new(null, TaskFilter.Deleted, TaskSort.Deleted))).Items);
        var range = DateRanges.For(TaskFilter.Today, DateTime.Now);
        Assert.Single((await repo.QueryAsync(new(null, TaskFilter.Today, TaskSort.Created, range.From, range.Until))).Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.RestoreTaskAsync(deleted.Id, original.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.UpdateContentAsync(deleted.Id, content, deleted.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.SetStatusAsync(deleted.Id, TodoStatus.Open, deleted.Revision));
        var restored = await repo.RestoreTaskAsync(deleted.Id, deleted.Revision);
        Assert.Equal(original.Status, restored.Status); Assert.Equal(original.CompletedAt, restored.CompletedAt);
        Assert.Equal(original.ContentJson, restored.ContentJson); Assert.Equal(original.ProjectId, restored.ProjectId);
        Assert.Null(restored.DeletedAt); Assert.Null(restored.PreviousStatus); Assert.Null(restored.PreviousCompletedAt);
        using var c = Database.Open(book.Path); BookLibrary.Validate(c, book.Path);
        Assert.Equal(status == TodoStatus.Open ? 2L : 3L, Database.Scalar(c, "SELECT COUNT(*) FROM TaskStateEvents"));
    }

    [Fact] public async Task MergeDoesNotResurrectDeletedTasksAndRestorationCanWin()
    {
        var book = await Library.CreateAsync("Book1", "本地"); var repo = new TaskRepository(book.Path);
        var task = await repo.AddTaskAsync(RichContent.FromText("任务"), null);
        var oldFile = Path.Combine(root, "old.db"); await Library.ExportAsync(book, oldFile);
        var deleted = await repo.DeleteTaskAsync(task.Id, task.Revision);
        for (int i = 0; i < 2; i++) { using var p = await Library.PrepareImportAsync(oldFile); await Library.ImportAsync(p, book, ImportMode.Merge); }
        Assert.Single((await repo.QueryAsync(new(null, TaskFilter.Deleted, TaskSort.Deleted))).Items);
        var newFile = Path.Combine(root, "new.db"); await Library.ExportAsync(book, newFile);
        var restored = await new TaskRepository(newFile).RestoreTaskAsync(deleted.Id, deleted.Revision);
        using (var p = await Library.PrepareImportAsync(newFile)) await Library.ImportAsync(p, book, ImportMode.Merge);
        Assert.Empty((await repo.QueryAsync(new(null, TaskFilter.Deleted, TaskSort.Deleted))).Items);
        Assert.Equal(restored.Status, Assert.Single((await repo.QueryAsync(new(null, TaskFilter.Open, TaskSort.Created))).Items).Status);
    }

    private string CreateV1(bool broken = false)
    {
        Directory.CreateDirectory(Library.DataDirectory); var path = Path.Combine(Library.DataDirectory, "Legacy.db");
        using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open();
        Database.Exec(c, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1.sql")));
        Database.Exec(c, $"PRAGMA application_id={Database.ApplicationId}; PRAGMA user_version=1; INSERT INTO BookMetadata VALUES(1,'Legacy','旧书');");
        if (broken) Database.Exec(c, "PRAGMA foreign_keys=OFF;");
        Database.Exec(c, "INSERT INTO Tasks VALUES($id,$project,$json,'旧任务',2,1000,2000,2000,2)",
            ("$id", new string('a', 32)), ("$project", broken ? "missing" : null), ("$json", RichContent.FromText("旧任务").ToJson()));
        Database.Exec(c, "INSERT INTO TaskStateEvents VALUES($id,$task,0,2,2000)", ("$id", new string('b', 32)), ("$task", new string('a', 32)));
        return path;
    }

    [Fact] public async Task V1UpgradeBacksUpAndPreservesHistory()
    {
        var path = CreateV1(); var book = Assert.Single(await Library.ListAsync());
        var task = Assert.Single((await new TaskRepository(path).QueryAsync(new(null, TaskFilter.Completed, TaskSort.Completed))).Items);
        Assert.Equal(2000L, task.CompletedAt); Assert.Equal(2L, task.Revision);
        using var c = Database.Open(path); Assert.Equal(2L, Database.Scalar(c, "PRAGMA user_version")); BookLibrary.Validate(c, path);
        using var backup = Database.Open(Assert.Single(Directory.GetFiles(Library.BackupDirectory, "*.db")));
        Assert.Equal(1L, Database.Scalar(backup, "PRAGMA user_version"));
        Assert.Equal(1L, Database.Scalar(c, "SELECT COUNT(*) FROM TaskStateEvents"));
    }

    [Fact] public async Task V1ImportUpgradesOnlyCopy()
    {
        var path = CreateV1(); using var p = await Library.PrepareImportAsync(path);
        using var original = Database.Open(path); using var stage = Database.Open(p.TemporaryPath);
        Assert.Equal(1L, Database.Scalar(original, "PRAGMA user_version")); Assert.Equal(2L, Database.Scalar(stage, "PRAGMA user_version"));
    }

    [Fact] public void FailedMigrationRollsBack()
    {
        var path = CreateV1(true); Assert.Throws<InvalidDataException>(() => Database.Upgrade(path, Library.BackupDirectory));
        using var c = Database.Open(path); Assert.Equal(1L, Database.Scalar(c, "PRAGMA user_version"));
        Assert.Equal(1L, Database.Scalar(c, "SELECT COUNT(*) FROM Tasks"));
        Assert.Equal(0L, Database.Scalar(c, "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('Tasks_v1','Events_v1')"));
    }
}
