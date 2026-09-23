using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.Tests;

public sealed class TaskFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "todo-fixes-" + Guid.NewGuid().ToString("N"));
    private BookLibrary Library => new(Path.Combine(_root, "Data"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task PermanentDeletionIsAtomicAndRemovesHistory()
    {
        var book = await Library.CreateAsync("Test", "测试"); var repo = new TaskRepository(book.Path);
        var a = await repo.AddTaskAsync(RichContent.FromText("A"), null);
        var b = await repo.AddTaskAsync(RichContent.FromText("B"), null);
        a = await repo.DeleteTaskAsync(a.Id, a.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.PermanentlyDeleteAsync(new Dictionary<string, long> { [a.Id] = a.Revision, [b.Id] = b.Revision }));
        b = await repo.DeleteTaskAsync(b.Id, b.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.PermanentlyDeleteAsync(new Dictionary<string, long> { [a.Id] = a.Revision, [b.Id] = b.Revision - 1 }));
        using (var c = Database.Open(book.Path))
        { Assert.Equal(2L, Database.Scalar(c, "SELECT COUNT(*) FROM Tasks")); Assert.Equal(2L, Database.Scalar(c, "SELECT COUNT(*) FROM TaskStateEvents")); }
        await repo.PermanentlyDeleteAsync(new Dictionary<string, long> { [a.Id] = a.Revision, [b.Id] = b.Revision });
        using var check = Database.Open(book.Path);
        Assert.Equal(0L, Database.Scalar(check, "SELECT COUNT(*) FROM Tasks"));
        Assert.Equal(0L, Database.Scalar(check, "SELECT COUNT(*) FROM TaskStateEvents"));
        Assert.Equal("ok", Database.Scalar(check, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task ExportAndMergeCarrySettingsAndAppendModulesInSourceOrder()
    {
        var book = await Library.CreateAsync("Test", "测试"); var repo = new TaskRepository(book.Path);
        var a = await repo.AddProjectAsync("A"); var b = await repo.AddProjectAsync("B");
        await repo.ReorderProjectsAsync([b.Id!, a.Id!]);
        await repo.SetSettingAsync("CompletedSort", "Created"); await repo.SetSettingAsync("StatusColor.Open", "#123456");
        var task = await repo.AddTaskAsync(RichContent.FromText("测试\n多行"), a.Id);
        task = await repo.SetStatusAsync(task.Id, TodoStatus.Verification, task.Revision);
        task = await repo.DeleteTaskAsync(task.Id, task.Revision);
        var export = Path.Combine(_root, "export.db"); await Library.ExportAsync(book, export);
        var fresh = new BookLibrary(Path.Combine(_root, "Fresh"));
        using (var prepared = await fresh.PrepareImportAsync(export))
        {
            var copy = await fresh.ImportAsync(prepared, null, ImportMode.Merge); var imported = new TaskRepository(copy.Path);
            Assert.Equal(new[] { "B", "A" }, (await imported.GetProjectsAsync()).Select(p => p.Name));
            Assert.Equal("Created", await imported.GetSettingAsync("CompletedSort"));
            Assert.Equal("#123456", await imported.GetSettingAsync("StatusColor.Open"));
            var restored = await imported.RestoreTaskAsync(task.Id, task.Revision);
            Assert.Equal(TodoStatus.Verification, restored.Status); Assert.Equal(task.ContentJson, restored.ContentJson);
        }
        var remote = new TaskRepository(export);
        var c = await remote.AddProjectAsync("C"); var d = await remote.AddProjectAsync("D");
        await remote.ReorderProjectsAsync([d.Id!, c.Id!, a.Id!, b.Id!]);
        await repo.ReorderProjectsAsync([a.Id!, b.Id!]);
        using (var prepared = await Library.PrepareImportAsync(export)) await Library.ImportAsync(prepared, book, ImportMode.Merge);
        Assert.Equal(new[] { "A", "B", "D", "C" }, (await repo.GetProjectsAsync()).Select(p => p.Name));
        var e = await repo.AddProjectAsync("E"); Assert.Equal(e.Id, (await repo.GetProjectsAsync())[^1].Id);
        var reopened = new TaskRepository(book.Path);
        Assert.Equal(task.ContentJson, (await reopened.QueryAsync(new(null, TaskFilter.Deleted, TaskSort.Deleted))).Items.Single().ContentJson);
    }
}
