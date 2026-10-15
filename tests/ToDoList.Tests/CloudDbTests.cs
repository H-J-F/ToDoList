using System.Text;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed partial class CloudSyncTests
{
    [Fact] public async Task CloudDbRetentionIsBoundedAndLongOfflinePeerStillMerges()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        var offline = await new TaskRepository(bookB.Path).AddTaskAsync(RichContent.FromText("长期离线新增"), null);
        var repo = new TaskRepository(bookA.Path);
        for (var i = 0; i < 12; i++)
        {
            task = await repo.UpdateContentAsync(task.Id, RichContent.FromText("多次同步 " + i), task.Revision);
            Assert.Null((await a.Sync()).CleanupWarning);
            Assert.True((await _cloud.ListAsync(CloudArea.Data)).Count <= 2);
        }
        a.Restart(); b.Restart(); await b.Sync(); await a.Sync();
        Assert.Equal(offline.PlainText, (await TaskById(bookA, offline.Id)).PlainText);
        Assert.Equal(task.PlainText, (await TaskById(bookB, task.Id)).PlainText);
        Assert.Empty(b.Coordinator.Conflicts);
        Assert.True(_cloud.Count <= 5);
        Assert.Single(await _cloud.ListAsync(CloudArea.Sync));
    }
    [Fact] public async Task FailedCleanupDoesNotFailSyncAndRetriesAfterRestart()
    {
        var (a, _, book, _, task) = await Pair(); var repo = new TaskRepository(book.Path);
        _cloud.FailCleanup = true;
        for (var i = 0; i < 3; i++) { task = await repo.UpdateContentAsync(task.Id, RichContent.FromText("更新 " + i), task.Revision); await a.Sync(); }
        var result = await a.Sync(); Assert.NotNull(result.CleanupWarning); Assert.NotNull(a.Coordinator.LastSyncedAt); Assert.False(a.Coordinator.HasPending);
        var statePath = Path.Combine(a.Books.StateDirectory, "state.json");
        Assert.NotEmpty(CloudJson.Parse<CloudSyncState>(File.ReadAllText(statePath)).Db!.CleanupPending);
        _cloud.FailCleanup = false; a.Restart(); Assert.Null((await a.Sync()).CleanupWarning);
        Assert.Equal(2, (await _cloud.ListAsync(CloudArea.Data)).Count);
        Assert.Empty(CloudJson.Parse<CloudSyncState>(File.ReadAllText(statePath)).Db!.CleanupPending);
    }
    private async Task<CloudCommit> SeedLegacy(CloudSnapshot snapshot, string? parent = null)
    {
        var bytes = SyncCoordinator.Encode(snapshot); var sha = CloudJson.Hash(bytes);
        var commit = new CloudCommit(1, FakeCloud.Account, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), parent == null ? [] : [parent], sha + ".snapshot.gz", sha);
        await _cloud.WriteImmutableAsync(CloudArea.Data, commit.SnapshotName, bytes);
        await _cloud.WriteImmutableAsync(CloudArea.Sync, "commit-" + commit.Id + ".json", Encoding.UTF8.GetBytes(CloudJson.Serialize(commit)));
        return commit;
    }
    [Fact] public async Task LegacyMigrationBacksUpAndRecyclesOnlyInventoriedFiles()
    {
        var a = NewPeer("A"); var book = await a.Library.CreateAsync("Legacy", "旧格式");
        await new TaskRepository(book.Path).AddTaskAsync(RichContent.FromText("完整内容"), null);
        var snapshot = await a.Books.CaptureAsync(); var commit = await SeedLegacy(snapshot);
        await _cloud.WriteImmutableAsync(CloudArea.Data, "user-unknown.db", [10, 11, 12]);
        await a.Sync();
        var sync = await _cloud.ListAsync(CloudArea.Sync); Assert.Single(sync); Assert.Matches(CloudDbCodec.ProtocolName, sync[0].Name);
        Assert.DoesNotContain(await _cloud.ListAsync(CloudArea.Data), f => f.Name == commit.SnapshotName);
        Assert.Contains(await _cloud.ListAsync(CloudArea.Data), f => f.Name == "user-unknown.db");
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(a.Books.StateDirectory, "legacy-backup"), "*.snapshot.gz", SearchOption.AllDirectories), p => Path.GetFileName(p) == commit.SnapshotName);
        var b = NewPeer("B"); await b.Sync(); Assert.Equal("完整内容", (await new TaskRepository((await b.Library.ListAsync()).Single().Path).QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items.Single().PlainText);
    }
    [Fact] public async Task LegacyOfflineModificationConflictsWithMigratedNewerVersion()
    {
        var a = NewPeer("A"); var book = await a.Library.CreateAsync("Legacy", "旧格式"); var repo = new TaskRepository(book.Path);
        var task = await repo.AddTaskAsync(RichContent.FromText("共同基线"), null); var baseline = await a.Books.CaptureAsync(); var first = await SeedLegacy(baseline);
        var b = NewPeer("B"); var bBook = await b.Library.CopyBookAsync(a.Library, book);
        CloudBookStorage.AtomicWrite(Path.Combine(b.Books.StateDirectory, "state.json"), CloudJson.Serialize(new CloudSyncState { AccountId = FakeCloud.Account, Baseline = baseline })); b.Restart();
        await new TaskRepository(bBook.Path).UpdateContentAsync(task.Id, RichContent.FromText("旧设备离线修改"), task.Revision);
        await repo.UpdateContentAsync(task.Id, RichContent.FromText("已迁移云端修改"), task.Revision); await SeedLegacy(await a.Books.CaptureAsync(), first.Id);
        await a.Sync(); await b.Sync();
        var conflict = b.Coordinator.Conflicts.Single(p => CloudSnapshot.Kind(p.Key) == "task");
        Assert.Equal(new[] { "旧设备离线修改", "已迁移云端修改" }.Order(), conflict.Value.Select(v => v.Read<TodoItem>().PlainText).Order());
    }
    [Fact] public async Task OldClientWritingAfterMigrationStopsNewSyncWithoutDeletingItsFiles()
    {
        var (a, _, _, _, _) = await Pair(); var commit = await SeedLegacy(await a.Books.CaptureAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => a.Sync());
        Assert.Contains(await _cloud.ListAsync(CloudArea.Sync), f => f.Name == "commit-" + commit.Id + ".json");
        Assert.Contains(await _cloud.ListAsync(CloudArea.Data), f => f.Name == commit.SnapshotName);
    }
    [Fact] public async Task LostMigrationResponseRecoversWithoutDeletingLegacyBeforeVerification()
    {
        var a = NewPeer("A"); await a.Library.CreateAsync("Legacy", "迁移中断");
        var original = await SeedLegacy(await a.Books.CaptureAsync()); _cloud.LoseProtocolResponse = true;
        await Assert.ThrowsAsync<IOException>(() => a.Sync());
        Assert.Contains(await _cloud.ListAsync(CloudArea.Sync), f => f.Name == "commit-" + original.Id + ".json");
        Assert.Contains(await _cloud.ListAsync(CloudArea.Data), f => f.Name == original.SnapshotName);
        a.Restart(); await a.Sync(); Assert.Single(await _cloud.ListAsync(CloudArea.Sync)); Assert.Empty(await _cloud.ListAsync(CloudArea.Data));
        Assert.Equal("迁移中断", (await a.Library.ListAsync()).Single().Title);
    }
    [Fact] public async Task MigratingLegacyBranchesPreservesAllConflictsAndResolutionSurvivesCompaction()
    {
        var a = NewPeer("A"); var book = await a.Library.CreateAsync("Legacy", "分支迁移");
        var task = await new TaskRepository(book.Path).AddTaskAsync(RichContent.FromText("共同内容"), null); var basis = await a.Books.CaptureAsync(); var original = await SeedLegacy(basis);
        CloudBookStorage.AtomicWrite(Path.Combine(a.Books.StateDirectory, "state.json"), CloudJson.Serialize(new CloudSyncState { AccountId = FakeCloud.Account, Baseline = basis })); a.Restart();
        foreach (var text in new[] { "旧分支 A", "旧分支 B" })
        {
            var branch = basis.Copy(); var content = RichContent.FromText(text);
            branch.Set(book.BookId, "task", task.Id, task with { ContentJson = content.ToJson(), PlainText = text, Revision = task.Revision + 1 });
            await SeedLegacy(branch, original.Id);
        }
        await a.Sync(); Assert.Single(a.Coordinator.Conflicts);
        var conflict = a.Coordinator.Conflicts.Single(); Assert.Equal(2, conflict.Value.Count);
        await a.Coordinator.ResolveAsync(conflict.Key, 0, true, a.Apply); await a.Sync();
        var b = NewPeer("B"); await b.Sync(); Assert.Empty(b.Coordinator.Conflicts);
        Assert.Equal(new[] { "旧分支 A", "旧分支 B" }.Order(), (await new TaskRepository((await b.Library.ListAsync()).Single().Path).QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items.Select(t => t.PlainText).Order());
        Assert.Single(await _cloud.ListAsync(CloudArea.Sync));
    }
    [Fact] public async Task DbCodecIsDeterministicAndRejectsWrongAccountAndInvalidCausality()
    {
        var a = NewPeer("A"); await a.Library.CreateAsync("Codecs", "编码"); var snapshot = await a.Books.CaptureAsync();
        var doc = new CloudDocument(); long n = 0; var device = Guid.NewGuid().ToString("N");
        foreach (var (key, values) in snapshot.Values) doc.Set(key, values, device, ref n);
        var envelope = new CloudDbEnvelope { AccountId = FakeCloud.Account, DeviceId = device, Document = doc, Generation = 1 };
        var bytes = CloudDbCodec.Encode(envelope); Assert.Equal(bytes, CloudDbCodec.Encode(envelope));
        Assert.Equal(CloudJson.Serialize(snapshot), CloudJson.Serialize((await CloudDbCodec.DecodeAsync(bytes, FakeCloud.Account)).Document.Snapshot()));
        await Assert.ThrowsAsync<InvalidDataException>(() => CloudDbCodec.DecodeAsync(bytes, new string('b', 64)));
        doc.Records.Values.First().Context[device] = 0; Assert.Throws<InvalidDataException>(() => doc.Validate());
    }
    [Fact] public async Task DuplicatedDeviceGenerationRejectsBothBranchesWithoutChoosingByFilename()
    {
        var (a, _, book, _, task) = await Pair(); var file = (await _cloud.ListAsync(CloudArea.Data)).Single();
        var envelope = await CloudDbCodec.DecodeAsync(await _cloud.ReadAsync(CloudArea.Data, file), FakeCloud.Account);
        var counter = envelope.Document.Records.Values.Select(r => r.Context.GetValueOrDefault(envelope.DeviceId)).Max();
        var text = RichContent.FromText("设备副本分支");
        envelope.Document.Set(CloudSnapshot.Key(book.BookId, "task", task.Id), [CloudValue.From(task with { ContentJson = text.ToJson(), PlainText = text.PlainText, Revision = task.Revision + 1 })], envelope.DeviceId, ref counter);
        var bytes = CloudDbCodec.Encode(envelope); var fork = "device-" + envelope.DeviceId + "-" + envelope.Generation.ToString("D20") + "-" + CloudJson.Hash(bytes) + ".db";
        await _cloud.WriteImmutableAsync(CloudArea.Data, fork, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => a.Sync());
        Assert.Equal(task.PlainText, (await TaskById(book, task.Id)).PlainText); Assert.Equal(2, (await _cloud.ListAsync(CloudArea.Data)).Count);
    }
    [Fact] public void DeletedContainerKeepsLatestLiveValueWhenMergingAStaleOfflineCopy()
    {
        var device = Guid.NewGuid().ToString("N"); var book = Guid.NewGuid().ToString("N"); var key = CloudSnapshot.Key(book, "book", book);
        var before = new CloudDocument(); long counter = 0; before.Set(key, [CloudValue.From(new CloudBookMeta("Book", "AAA 旧标题"))], device, ref counter);
        var after = before.Copy(); after.Set(key, [CloudValue.From(new CloudBookMeta("Book", "ZZZ 最新标题"))], device, ref counter); after.Set(key, [new(null)], device, ref counter);
        Assert.Equal("ZZZ 最新标题", CloudDocument.Merge(before, after).Records[key].LastLive!.Read<CloudBookMeta>().Title);
        Assert.Equal("ZZZ 最新标题", CloudDocument.Merge(after, before).Records[key].LastLive!.Read<CloudBookMeta>().Title);
    }
    [Fact] public async Task CausalMergeIsCommutativeAssociativeAndIdempotent()
    {
        var a = NewPeer("A"); var book = await a.Library.CreateAsync("Algebra", "合并代数");
        var key = CloudSnapshot.Key(book.BookId, "book", book.BookId); var value = (await a.Books.CaptureAsync()).Values[key];
        var seed = new CloudDocument(); var seedId = Guid.NewGuid().ToString("N"); long seedCounter = 0; seed.Set(key, value, seedId, ref seedCounter);
        var versions = Enumerable.Range(0, 3).Select(i =>
        { var doc = seed.Copy(); long n = 0; doc.Set(key, [CloudValue.From(new CloudBookMeta(book.Name, "版本 " + i))], Guid.NewGuid().ToString("N"), ref n); return doc; }).ToArray();
        string Canonical(CloudDocument doc) => CloudJson.Serialize(doc.Snapshot());
        Assert.Equal(Canonical(CloudDocument.Merge(versions[0], versions[1])), Canonical(CloudDocument.Merge(versions[1], versions[0])));
        Assert.Equal(Canonical(CloudDocument.Merge(CloudDocument.Merge(versions[0], versions[1]), versions[2])), Canonical(CloudDocument.Merge(versions[0], CloudDocument.Merge(versions[1], versions[2]))));
        Assert.Equal(Canonical(versions[0]), Canonical(CloudDocument.Merge(versions[0], versions[0])));
    }
}
