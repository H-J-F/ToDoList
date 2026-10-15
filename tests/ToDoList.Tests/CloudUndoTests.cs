using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed partial class CloudSyncTests
{
    [Fact] public async Task UndoSyncedModuleDeletionRestoresContainerAndMembershipOnBothPeers()
    {
        var (a, b, bookA, bookB, task) = await Pair(); LocalUndo? undo = null;
        var repo = new TaskRepository(bookA.Path) { EnableUndo = true }; repo.UndoCommitted += r => undo = r;
        var project = await repo.AddProjectAsync("可恢复模块"); await repo.UpdateTaskAsync(task.Id, RichContent.FromText(task.PlainText), project.Id, task.Revision);
        await a.Sync(); await b.Sync(); await repo.DeleteProjectAsync(project.Id!); var deletion = undo!;
        await a.Sync(); await b.Sync(); Assert.Null((await TaskById(bookB, task.Id)).ProjectId);
        await deletion.UndoAsync(); await a.Sync(); await b.Sync();
        Assert.Equal(project.Id, (await TaskById(bookB, task.Id)).ProjectId); Assert.Equal(project, Assert.Single(await new TaskRepository(bookB.Path).GetProjectsAsync()));
        Assert.Empty(a.Coordinator.Conflicts); Assert.Empty(b.Coordinator.Conflicts);
    }
    [Fact] public async Task UnrelatedRemoteAdditionSurvivesUndoButRemoteEditOfSameTaskRejectsIt()
    {
        var (a, b, bookA, bookB, task) = await Pair(); LocalUndo? undo = null;
        var repo = new TaskRepository(bookA.Path) { EnableUndo = true }; repo.UndoCommitted += r => undo = r;
        await repo.UpdateContentAsync(task.Id, RichContent.FromText("本地修改"), task.Revision); var edit = undo!;
        var other = await new TaskRepository(bookB.Path).AddTaskAsync(RichContent.FromText("远端新增"), null); await b.Sync(); await a.Sync();
        await edit.UndoAsync(); await a.Sync(); await b.Sync(); Assert.Equal(other.PlainText, (await TaskById(bookA, other.Id)).PlainText);
        var current = await TaskById(bookA, task.Id); await repo.UpdateContentAsync(task.Id, RichContent.FromText("下一次修改"), current.Revision); var latest = undo!;
        await a.Sync(); await b.Sync(); var remote = await TaskById(bookB, task.Id);
        await new TaskRepository(bookB.Path).UpdateContentAsync(task.Id, RichContent.FromText("远端后续修改"), remote.Revision); await b.Sync(); await a.Sync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => latest.UndoAsync()); Assert.Equal("远端后续修改", (await TaskById(bookA, task.Id)).PlainText);
    }
    [Fact] public async Task UndoUploadedCombinedAddConvergesWithoutResurrectingHistory()
    {
        var (a, b, bookA, bookB, _) = await Pair(); LocalUndo? undo = null;
        var repo = new TaskRepository(bookA.Path) { EnableUndo = true }; repo.UndoCommitted += r => undo = r;
        var task = await repo.AddTaskAsync(RichContent.FromText("同步后撤回"), null, "新增模块"); var addUndo = undo!;
        await a.Sync(); await b.Sync(); Assert.Equal(task.PlainText, (await TaskById(bookB, task.Id)).PlainText);
        await addUndo.UndoAsync(); await a.Sync(); await b.Sync(); await a.Sync();
        var tasks = (await new TaskRepository(bookB.Path).QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items;
        Assert.DoesNotContain(tasks, t => t.Id == task.Id); Assert.Empty(await new TaskRepository(bookB.Path).GetProjectsAsync());
        Assert.Empty(a.Coordinator.Conflicts); Assert.Empty(b.Coordinator.Conflicts);
    }
    [Fact] public async Task UndoDuringApplyRejectsStaleSnapshotAndNextSyncConverges()
    {
        var (a, b, bookA, bookB, task) = await Pair(); LocalUndo? undo = null;
        var repo = new TaskRepository(bookA.Path) { EnableUndo = true }; repo.UndoCommitted += r => undo = r;
        await repo.UpdateContentAsync(task.Id, RichContent.FromText("待上传"), task.Revision);
        await Assert.ThrowsAsync<CloudLocalChangedException>(() => a.Coordinator.SyncAsync(SyncDirection.Both, async (expected, desired, ct) =>
        { await undo!.UndoAsync(ct); return await a.Apply(expected, desired, ct); }));
        await a.Sync(); await b.Sync(); Assert.Equal(task.PlainText, (await TaskById(bookB, task.Id)).PlainText); Assert.Empty(a.Coordinator.Conflicts);
    }
    [Fact] public async Task UndoLostCommitResponseRetainsPendingAndSendsCompensatingChange()
    {
        var (a, b, bookA, bookB, task) = await Pair(); LocalUndo? undo = null;
        var repo = new TaskRepository(bookA.Path) { EnableUndo = true }; repo.UndoCommitted += r => undo = r;
        await repo.SetStatusAsync(task.Id, TodoStatus.Completed, task.Revision); _cloud.LoseCommitResponse = true;
        await Assert.ThrowsAsync<IOException>(() => a.Sync()); Assert.True(a.Coordinator.HasPending);
        await undo!.UndoAsync(); a.Restart(); await a.Sync(); await b.Sync();
        Assert.Equal(TodoStatus.Open, (await TaskById(bookB, task.Id)).Status); Assert.False(a.Coordinator.HasPending); Assert.Empty(b.Coordinator.Conflicts);
        Assert.DoesNotContain((await b.Books.CaptureAsync()).Values, p => CloudSnapshot.Kind(p.Key) == "event" && p.Value.Any(v => !v.Deleted));
    }
}
