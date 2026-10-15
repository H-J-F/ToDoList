using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed class CloudLocalImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "todo-local-import-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Theory]
    [InlineData("原来的笔记", false)]
    [InlineData("原来的笔记", true)]
    [InlineData("保留本地副本文字的原标题（本地副本）", true)]
    [InlineData(null, true)]
    public async Task GuestImportPreservesTitleAndAllDataWithoutChangingSource(string? title, bool collision)
    {
        title ??= new string('书', 100);
        var guest = new BookLibrary(Path.Combine(_root, "Guest"));
        var account = new BookLibrary(Path.Combine(_root, "Account"));
        var book = await guest.CreateAsync("Work", title);
        var repository = new TaskRepository(book.Path);
        var project = await repository.AddProjectAsync("原模块");
        var task = await repository.AddTaskAsync(new(1, [new([new("粗体内容", Bold: true), new("链接", Link: "https://example.com")])]), project.Id);
        task = await repository.UpdateContentAsync(task.Id, RichContent.FromText("修改后的内容"), task.Revision);
        await repository.SetStatusAsync(task.Id, TodoStatus.Completed, task.Revision);
        await repository.AddTaskAsync(new(1, [new([new("格式保留", Italic: true, Color: "#123456")])]), project.Id);
        var deleted = await repository.AddTaskAsync(RichContent.FromText("回收站内容"), null);
        await repository.DeleteTaskAsync(deleted.Id, deleted.Revision);
        await repository.SetSettingAsync("StatusColor.Open", "#123456");
        var source = new CloudBookStorage(guest);
        var before = await source.CaptureAsync();
        BookInfo? existing = collision ? await account.CreateAsync("Work", "已有笔记") : null;
        var destination = new CloudBookStorage(account);
        var existingBefore = await destination.CaptureAsync();

        var copied = await account.CopyBookAsync(guest, book);

        Assert.Equal(title, copied.Title);
        Assert.Equal(collision ? 2 : 1, (await account.ListAsync()).Count);
        if (collision) Assert.NotEqual(book.Name, copied.Name);
        else Assert.Equal(book.Name, copied.Name);
        Assert.Equal(CloudJson.Serialize(before), CloudJson.Serialize(await source.CaptureAsync()));
        var after = await destination.CaptureAsync();
        foreach (var (key, values) in existingBefore.Values)
            Assert.Equal(values.Select(v => v.Json), after.Values[key].Select(v => v.Json));
        var importedBook = after.Values.Single(p => CloudSnapshot.Kind(p.Key) == "book" && p.Value[0].Read<CloudBookMeta>().Title == title);
        var importedId = CloudSnapshot.Book(importedBook.Key);
        Assert.Equal(book.BookId, importedId);
        foreach (var (key, values) in before.Values.Where(p => CloudSnapshot.Kind(p.Key) != "book"))
            Assert.Equal(values.Select(v => v.Json), after.Values[CloudSnapshot.Key(importedId, CloudSnapshot.Kind(key), CloudSnapshot.Id(key))].Select(v => v.Json));
        Assert.Equal(before.Values.Count, after.Values.Count - existingBefore.Values.Count);
    }
}
