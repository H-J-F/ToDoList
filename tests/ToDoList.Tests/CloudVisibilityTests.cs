using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed partial class CloudSyncTests
{
    [Fact] public async Task HidingAndRestoringConvergesWithoutRemovingBookContents()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        await new TaskRepository(bookA.Path).SetSettingAsync(BookInfo.HiddenSetting, "true"); await a.Sync(); await b.Sync();
        Assert.True(Assert.Single(await b.Library.ListAsync()).IsHidden);
        Assert.Equal(task.PlainText, (await TaskById(bookB, task.Id)).PlainText);
        await new TaskRepository(bookB.Path).SetSettingAsync(BookInfo.HiddenSetting, "false"); await b.Sync(); await a.Sync();
        Assert.False(Assert.Single(await a.Library.ListAsync()).IsHidden); Assert.Empty(a.Coordinator.Conflicts);
    }
    [Fact] public async Task ConcurrentVisibilityChangesUseExistingSettingConflictResolution()
    {
        var (a, b, bookA, bookB, _) = await Pair();
        await new TaskRepository(bookA.Path).SetSettingAsync(BookInfo.HiddenSetting, "true");
        await new TaskRepository(bookB.Path).SetSettingAsync(BookInfo.HiddenSetting, "false");
        _cloud.Freeze(); await a.Sync(); await b.Sync(); _cloud.Unfreeze(); await a.Sync(); await b.Sync();
        var conflict = a.Coordinator.Conflicts.Single(p => CloudSnapshot.Kind(p.Key) == "setting" && CloudSnapshot.Id(p.Key) == BookInfo.HiddenSetting);
        Assert.Equal(2, conflict.Value.Count);
        var visible = conflict.Value.FindIndex(v => v.Read<string>() == "false");
        await a.Coordinator.ResolveAsync(conflict.Key, visible, false, a.Apply); await a.Sync(); await b.Sync();
        Assert.False(Assert.Single(await a.Library.ListAsync()).IsHidden); Assert.False(Assert.Single(await b.Library.ListAsync()).IsHidden);
    }
}
