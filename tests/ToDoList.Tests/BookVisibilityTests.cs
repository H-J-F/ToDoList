using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed class BookVisibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "todo-visibility-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Fact] public async Task HiddenBookRetainsIdentityContentExportAndCopy()
    {
        var library = new BookLibrary(Path.Combine(_root, "source")); var book = await library.CreateAsync("Hidden", "同名笔记");
        Assert.False(book.IsHidden); var repo = new TaskRepository(book.Path);
        var task = await repo.AddTaskAsync(RichContent.FromText("隐藏不删除内容"), null, "模块");
        var deleted = await repo.DeleteTaskAsync(task.Id, task.Revision);
        await repo.SetSettingAsync(BookInfo.HiddenSetting, "true");
        book = Assert.Single(await new BookLibrary(library.DataDirectory).ListAsync());
        Assert.True(book.IsHidden); Assert.Equal("同名笔记", book.Title); Assert.EndsWith("（已隐藏）", book.DisplayTitle);
        var snapshot = await new CloudBookStorage(library).CaptureAsync();
        Assert.Equal("true", snapshot.Values[CloudSnapshot.Key(book.BookId, "setting", BookInfo.HiddenSetting)][0].Read<string>());
        var export = Path.Combine(_root, "export.db"); await library.ExportAsync(book, export);
        using var prepared = await library.PrepareImportAsync(export); Assert.True(prepared.Info.IsHidden);
        var destination = new BookLibrary(Path.Combine(_root, "destination")); var copy = await destination.CopyBookAsync(library, book);
        Assert.True(copy.IsHidden); Assert.Equal(book.BookId, copy.BookId);
        Assert.Equal(deleted.Id, Assert.Single((await new TaskRepository(copy.Path).QueryAsync(new(null, TaskFilter.Deleted, TaskSort.Created))).Items).Id);
        await repo.SetSettingAsync(BookInfo.HiddenSetting, "false"); Assert.False(Assert.Single(await library.ListAsync()).IsHidden);
        Assert.True(Assert.Single(await destination.ListAsync()).IsHidden);
    }
}
