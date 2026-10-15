using System.Net;
using System.Text;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed partial class CloudSyncTests
{
    [Fact] public async Task UnchangedPeersOnlyListAndNeverApplyDownloadOrUpload()
    {
        var (a, _, _, _, _) = await Pair(); await a.Sync(); a.Restart(); _cloud.ResetCalls(); var applied = 0;
        var result = await a.Coordinator.SyncAsync(SyncDirection.Both, (_, _, _) => { applied++; return Task.FromResult(true); });
        Assert.Equal(CloudSyncOutcome.Unchanged, result.Outcome); Assert.Equal(0, applied);
        Assert.Equal(2, _cloud.ListCalls); Assert.Equal(0, _cloud.ReadCalls); Assert.Equal(0, _cloud.WriteCalls);
        _cloud.ResetCalls(); Assert.Equal(CloudSyncOutcome.Unchanged, (await a.Sync(SyncDirection.Upload)).Outcome);
        Assert.Equal(0, _cloud.InitializeCalls); Assert.Equal(0, _cloud.ListCalls);
    }
    [Fact] public async Task RemoteOnlyChangeIsDetectedDespiteUnchangedLocalSnapshot()
    {
        var (a, b, book, remoteBook, task) = await Pair(); await a.Sync();
        await new TaskRepository(remoteBook.Path).UpdateContentAsync(task.Id, RichContent.FromText("其他设备修改"), task.Revision); await b.Sync();
        _cloud.ResetCalls(); Assert.Equal(CloudSyncOutcome.Synced, (await a.Sync()).Outcome);
        Assert.Equal("其他设备修改", (await TaskById(book, task.Id)).PlainText); Assert.True(_cloud.ReadCalls > 0);
        _cloud.ResetCalls(); Assert.Equal(CloudSyncOutcome.Unchanged, (await a.Sync()).Outcome); Assert.Equal(0, _cloud.ReadCalls);
    }
    [Fact] public async Task MissingOrCorruptCheckpointRequiresFullValidation()
    {
        var (a, _, _, _, _) = await Pair(); await a.Sync();
        var state = CloudJson.Parse<CloudSyncState>(File.ReadAllText(Path.Combine(a.Books.StateDirectory, "state.json")));
        var path = Path.Combine(a.Books.StateDirectory, "checkpoints", "local-" + state.LocalCheckpoint + ".json");
        File.WriteAllText(path, "corrupt"); a.Restart(); Assert.Equal(CloudSyncOutcome.Synced, (await a.Sync()).Outcome);
        File.Delete(path); a.Restart(); Assert.Equal(CloudSyncOutcome.Synced, (await a.Sync()).Outcome);
    }
    [Fact] public async Task MutationDuringUploadCannotAdvanceCheckpointToUnsyncedVersion()
    {
        var (a, b, book, _, task) = await Pair(); var repo = new TaskRepository(book.Path);
        task = await repo.UpdateContentAsync(task.Id, RichContent.FromText("发送版本"), task.Revision);
        await a.Coordinator.SyncAsync(SyncDirection.Both, async (expected, desired, ct) =>
        { await a.Apply(expected, desired, ct); await repo.UpdateContentAsync(task.Id, RichContent.FromText("后续修改"), task.Revision); return true; });
        Assert.Equal(CloudSyncOutcome.Synced, (await a.Sync()).Outcome); await b.Sync();
        Assert.Equal("后续修改", (await TaskById((await b.Library.ListAsync()).Single(), task.Id)).PlainText);
    }
    [Theory] [InlineData(CloudFailureKind.DailyQuota, 117401)] [InlineData(CloudFailureKind.RateLimit, 429)] [InlineData(CloudFailureKind.Disabled, 117407)]
    public async Task CoolingPersistsAcrossRestartAndManualCallsMakeNoRequests(CloudFailureKind kind, int code)
    {
        var a = NewPeer("Cooling"); await a.Library.CreateAsync("Cooling", "冷却测试");
        _cloud.InitializeFailure = new("simulated restriction", HttpStatusCode.OK, kind, code);
        await Assert.ThrowsAsync<CloudTransportException>(() => a.Sync()); Assert.Equal(1, _cloud.InitializeCalls);
        Assert.Null(a.Coordinator.LastSyncedAt); a.Restart(); _cloud.InitializeFailure = null; _cloud.ResetCalls();
        await Assert.ThrowsAsync<CloudTransportException>(() => a.Sync()); Assert.Equal(0, _cloud.InitializeCalls);
        if (kind != CloudFailureKind.Disabled) Assert.True(a.Coordinator.RetryAt > DateTimeOffset.UtcNow);
    }
    [Fact] public async Task DeferredOrFailedSynchronizationDoesNotAcknowledgeCheckpoint()
    {
        var (a, _, book, _, task) = await Pair(); var path = Path.Combine(a.Books.StateDirectory, "state.json");
        var before = CloudJson.Parse<CloudSyncState>(File.ReadAllText(path)).LocalCheckpoint;
        await new TaskRepository(book.Path).UpdateContentAsync(task.Id, RichContent.FromText("等待同步"), task.Revision);
        var result = await a.Coordinator.SyncAsync(SyncDirection.Both, (_, _, _) => Task.FromResult(false));
        Assert.Equal(CloudSyncOutcome.Deferred, result.Outcome);
        Assert.Equal(before, CloudJson.Parse<CloudSyncState>(File.ReadAllText(path)).LocalCheckpoint);
        _cloud.LoseCommitResponse = true; await Assert.ThrowsAsync<IOException>(() => a.Sync());
        Assert.Equal(before, CloudJson.Parse<CloudSyncState>(File.ReadAllText(path)).LocalCheckpoint);
        a.Restart(); Assert.Equal(CloudSyncOutcome.Synced, (await a.Sync()).Outcome);
    }
    [Fact] public async Task CheckpointReadsWalAndIgnoresLocalAppearanceAndFileLayout()
    {
        var a = NewPeer("Wal"); var book = await a.Library.CreateAsync("Wal", "日志外观"); var repo = new TaskRepository(book.Path);
        var empty = SyncCoordinator.LocalCheckpointBytes(await a.Books.CaptureAsync());
        await repo.AddTaskAsync(RichContent.FromText("事务最新内容"), null);
        var bytes = SyncCoordinator.LocalCheckpointBytes(await a.Books.CaptureAsync()); Assert.NotEqual(empty, bytes);
        var settings = a.Library.LoadSettings(); settings.FontSize = 18; a.Library.SaveSettings(settings);
        using var log = new FileRuntimeLog(a.Library.DataDirectory); log.Write("test", null, null, null);
        Assert.Equal(bytes, SyncCoordinator.LocalCheckpointBytes(await a.Books.CaptureAsync()));
        await Database.RunAsync(book.Path, () => { using var c = Database.Open(book.Path); Database.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)"); return true; });
        Assert.Equal(bytes, SyncCoordinator.LocalCheckpointBytes(await a.Books.CaptureAsync()));
    }
}
