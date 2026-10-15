using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed class UndoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "todo-undo-" + Guid.NewGuid().ToString("N"));
    private BookLibrary Library => new(_root);
    public void Dispose()
    {
        var path = Path.GetFullPath(_root);
        if (path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).StartsWith("todo-undo-", StringComparison.Ordinal) && Directory.Exists(path)) Directory.Delete(path, true);
    }
    private async Task<(TaskRepository Repo, Func<LocalUndo> Receipt)> Setup()
    {
        var book = await Library.CreateAsync("Undo", "撤回"); var repo = new TaskRepository(book.Path) { EnableUndo = true };
        LocalUndo? receipt = null; repo.UndoCommitted += r => receipt = r;
        return (repo, () => receipt ?? throw new Exception("No receipt"));
    }
    private static async Task<TodoItem> Item(TaskRepository repo, string id) => (await repo.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items.Single(t => t.Id == id);
    [Fact] public async Task CombinedAddIsOneShotAndProducesSyncTombstones()
    {
        var (repo, receipt) = await Setup(); var task = await repo.AddTaskAsync(RichContent.FromText("组合新增"), null, "新模块");
        var undo = receipt(); Assert.Contains("task", undo.Kinds); Assert.Contains("project", undo.Kinds);
        await undo.UndoAsync(); Assert.Empty(await repo.GetProjectsAsync()); Assert.Empty((await repo.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items);
        var snapshot = await new CloudBookStorage(Library).CaptureAsync();
        Assert.Contains(snapshot.Values, p => CloudSnapshot.Kind(p.Key) == "task" && p.Value[0].Deleted);
        Assert.Contains(snapshot.Values, p => CloudSnapshot.Kind(p.Key) == "project" && p.Value[0].Deleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => undo.UndoAsync());
    }
    [Fact] public async Task ContentAndMembershipRestoreWithoutRewindingRevisionOrLosingUnrelatedWrite()
    {
        var (repo, receipt) = await Setup(); var project = await repo.AddProjectAsync("模块");
        var initial = await repo.AddTaskAsync(RichContent.FromText("原文"), null);
        var rich = new RichContent(1, [new([new("加粗", Bold: true)])]);
        var edited = await repo.UpdateTaskAsync(initial.Id, rich, project.Id, initial.Revision); var undo = receipt();
        var independent = await new TaskRepository(repo.Path).AddTaskAsync(RichContent.FromText("无关新增"), null);
        await undo.UndoAsync(); var restored = await Item(repo, initial.Id);
        Assert.Equal(initial.ContentJson, restored.ContentJson); Assert.Null(restored.ProjectId); Assert.Null(restored.EditedAt);
        Assert.Equal(initial.CreatedAt, restored.CreatedAt); Assert.True(restored.Revision > edited.Revision); Assert.True(restored.UpdatedAt > edited.UpdatedAt);
        Assert.Equal(independent, await Item(repo, independent.Id));
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task StatusDeleteRestoreUndoRestoresAllFieldsAndRemovesCanceledHistory(int operation)
    {
        var (repo, receipt) = await Setup(); var task = await repo.AddTaskAsync(RichContent.FromText("状态"), null);
        task = await repo.SetStatusAsync(task.Id, TodoStatus.Completed, task.Revision);
        if (operation == 2) task = await repo.DeleteTaskAsync(task.Id, task.Revision);
        var before = await new CloudBookStorage(Library).CaptureAsync();
        if (operation == 0) await repo.SetStatusAsync(task.Id, TodoStatus.Open, task.Revision);
        else if (operation == 1) await repo.DeleteTaskAsync(task.Id, task.Revision);
        else await repo.RestoreTaskAsync(task.Id, task.Revision);
        await receipt().UndoAsync(); var restored = await Item(repo, task.Id);
        Assert.Equal(task with { Revision = restored.Revision, UpdatedAt = restored.UpdatedAt }, restored);
        var after = await new CloudBookStorage(Library).CaptureAsync();
        var oldEvents = before.Values.Where(p => CloudSnapshot.Kind(p.Key) == "event" && !p.Value[0].Deleted).Select(p => p.Key).Order();
        Assert.Equal(oldEvents, after.Values.Where(p => CloudSnapshot.Kind(p.Key) == "event" && !p.Value[0].Deleted).Select(p => p.Key).Order());
        Assert.Contains(after.Values, p => CloudSnapshot.Kind(p.Key) == "event" && p.Value[0].Deleted);
    }
    [Fact] public async Task ModuleDeleteRestoresMembershipOrderAndDeletionMarker()
    {
        var (repo, receipt) = await Setup(); var a = await repo.AddProjectAsync("A"); var b = await repo.AddProjectAsync("B");
        var task = await repo.AddTaskAsync(RichContent.FromText("归属"), a.Id);
        await repo.DeleteProjectAsync(a.Id!); await receipt().UndoAsync();
        Assert.Equal(new[] { a, b }, await repo.GetProjectsAsync()); var restored = await Item(repo, task.Id);
        Assert.Equal(a.Id, restored.ProjectId); Assert.Equal(task.EditedAt, restored.EditedAt);
        var snapshot = await new CloudBookStorage(Library).CaptureAsync();
        Assert.DoesNotContain(snapshot.Values, p => CloudSnapshot.Kind(p.Key) == "project" && p.Value[0].Deleted);
    }
    [Fact] public async Task ModuleRenameOrderAndAddUndoAndNoOpDoesNotReplaceReceipt()
    {
        var (repo, receipt) = await Setup(); var a = await repo.AddProjectAsync("A"); var b = await repo.AddProjectAsync("B");
        await repo.RenameProjectAsync(a.Id!, "改名"); await receipt().UndoAsync(); Assert.Equal(a, (await repo.GetProjectsAsync())[0]);
        await repo.ReorderProjectsAsync([b.Id!, a.Id!]); var orderReceipt = receipt();
        await repo.ReorderProjectsAsync([b.Id!, a.Id!]); Assert.Same(orderReceipt, receipt());
        await orderReceipt.UndoAsync(); Assert.Equal(new[] { a, b }, await repo.GetProjectsAsync());
        var c = await repo.AddProjectAsync("C"); var addReceipt = receipt(); await repo.RenameProjectAsync(c.Id!, c.Name); Assert.Same(addReceipt, receipt());
        await addReceipt.UndoAsync(); Assert.Equal(new[] { a, b }, await repo.GetProjectsAsync());
    }
    [Fact] public async Task ConflictingWriteRejectsAllOfBulkUndo()
    {
        var (repo, receipt) = await Setup(); var p = await repo.AddProjectAsync("P");
        var a = await repo.AddTaskAsync(RichContent.FromText("A"), p.Id); var b = await repo.AddTaskAsync(RichContent.FromText("B"), p.Id);
        await repo.DeleteProjectAsync(p.Id!); var undo = receipt(); var changed = await Item(repo, b.Id);
        await new TaskRepository(repo.Path).UpdateContentAsync(b.Id, RichContent.FromText("同步修改"), changed.Revision);
        var before = CloudJson.Serialize(await new CloudBookStorage(Library).CaptureAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => undo.UndoAsync());
        Assert.Equal(before, CloudJson.Serialize(await new CloudBookStorage(Library).CaptureAsync())); Assert.Null((await Item(repo, a.Id)).ProjectId);
    }
    [Fact] public async Task FailedWriteAndUnchangedTaskDoNotCreateReceipt()
    {
        var (repo, receipt) = await Setup(); var task = await repo.AddTaskAsync(RichContent.FromText("原文"), null); var previous = receipt();
        await repo.UpdateTaskAsync(task.Id, RichContent.FromText(task.PlainText), null, task.Revision); Assert.Same(previous, receipt());
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.UpdateTaskAsync(task.Id, RichContent.FromText("错误修订"), null, 999)); Assert.Same(previous, receipt());
        Assert.Equal(task, await Item(repo, task.Id));
    }
    private sealed class Clock : TimeProvider
    {
        public long Tick; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Tick;
    }
    [Fact] public async Task AcceptedUndoWaitsForStorageLockAndThenCommitsOnce()
    {
        var (repo, receipt) = await Setup(); await repo.AddTaskAsync(RichContent.FromText("锁等待"), null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = Database.RunAsync(repo.Path, () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return true; });
        await entered.Task; var undo = receipt().UndoAsync();
        try { Assert.False(undo.IsCompleted); }
        finally { release.TrySetResult(); }
        await holding; await undo; Assert.Empty((await repo.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items);
    }
    [Fact] public void UndoDeadlineReplacementAcceptanceAndSyncUseMonotonicTime()
    {
        var clock = new Clock(); var window = new UndoWindow(clock); window.Start(true); var first = window.Generation;
        clock.Tick = 4499; Assert.True(window.CanUndo); window.Synced(); Assert.True(window.EnteredSync); Assert.True(window.CanUndo);
        clock.Tick = 4500; Assert.False(window.CanUndo); Assert.Equal(0, window.Remaining); Assert.False(window.ShouldFade); Assert.False(window.Accept());
        clock.Tick = 5000; Assert.True(window.ShouldFade); window.Start(true); Assert.True(window.Generation > first); Assert.Equal(1, window.Remaining);
        Assert.True(window.Accept()); Assert.False(window.Accept()); clock.Tick += 10000; Assert.False(window.ShouldFade);
        window.Start(false); clock.Tick += 1999; Assert.False(window.ShouldFade); clock.Tick++; Assert.True(window.ShouldFade);
    }
    [Fact] public void OldSyncCannotAcknowledgeUndoOrLaterMutationEvenWithIdenticalTimestamps()
    {
        var now = DateTime.UtcNow; var schedule = new CloudSyncSchedule(); schedule.Changed(now); var start = schedule.Generation;
        schedule.Changed(now); schedule.Completed(now, 5, start, false); Assert.Equal(now, schedule.DirtyAt);
        Assert.False(schedule.Due(now.AddSeconds(4.999), true)); Assert.False(schedule.Due(now.AddSeconds(6), true));
        schedule.CancelSave(now, 5, start); Assert.NotNull(schedule.DirtyAt);
        schedule.CancelSave(now, 5, schedule.Generation); Assert.Null(schedule.DirtyAt); Assert.False(schedule.Due(now.AddSeconds(5), true));
        schedule.Changed(now.AddSeconds(6)); Assert.False(schedule.Due(now.AddSeconds(11), true)); Assert.True(schedule.Due(now.AddMinutes(5), true)); Assert.False(schedule.Due(now.AddMinutes(5), false));
    }
}
