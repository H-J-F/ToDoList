using System.Security.Cryptography;
using System.Text;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed partial class CloudSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "todo-cloud-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCloud _cloud = new();
    private readonly List<Peer> _peers = [];
    public void Dispose()
    {
        var path = Path.GetFullPath(_root);
        if (path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).StartsWith("todo-cloud-tests-", StringComparison.Ordinal))
            if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    private Peer NewPeer(string name, string? account = null)
    {
        var peer = new Peer(new(Path.Combine(_root, name)), _cloud, account ?? FakeCloud.Account); _peers.Add(peer); return peer;
    }
    private static async Task<TodoItem> TaskById(BookInfo book, string id) => (await new TaskRepository(book.Path).QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items.Single(t => t.Id == id);
    private async Task<(Peer A, Peer B, BookInfo BookA, BookInfo BookB, TodoItem Task)> Pair()
    {
        var a = NewPeer("A"); var b = NewPeer("B"); var book = await a.Library.CreateAsync("Work", "工作");
        var task = await new TaskRepository(book.Path).AddTaskAsync(RichContent.FromText("初始"), null);
        await a.Sync(); await b.Sync(); return (a, b, book, Assert.Single(await b.Library.ListAsync()), task);
    }
    [Fact] public async Task FreshDeviceDownloadsAllDataAndPreservesIdentity()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        Assert.Equal(task.PlainText, (await TaskById(bookB, task.Id)).PlainText);
        Assert.Equal((await a.Books.CaptureAsync()).Values.Keys, (await b.Books.CaptureAsync()).Values.Keys);
        Assert.Empty(b.Coordinator.Conflicts);
        var count = _cloud.Count; await a.Sync(); await b.Sync(); Assert.Equal(count, _cloud.Count);
    }
    [Fact] public async Task ConcurrentDifferentTasksMergeWithoutClockPrecedence()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        var left = await new TaskRepository(bookA.Path).UpdateContentAsync(task.Id, RichContent.FromText("A 修改"), task.Revision);
        var right = await new TaskRepository(bookB.Path).AddTaskAsync(RichContent.FromText("B 新建"), null);
        _cloud.Freeze(); await a.Sync(); await b.Sync(); _cloud.Unfreeze();
        await a.Sync(); await b.Sync();
        Assert.Equal(left.PlainText, (await TaskById(bookB, task.Id)).PlainText);
        Assert.Equal(right.PlainText, (await TaskById(bookA, right.Id)).PlainText);
        Assert.Empty(a.Coordinator.Conflicts); Assert.Empty(b.Coordinator.Conflicts);
    }
    [Fact] public async Task SameTaskConflictSurvivesRestartAndKeepingBothConverges()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        await new TaskRepository(bookA.Path).UpdateContentAsync(task.Id, RichContent.FromText("左版本"), task.Revision);
        await new TaskRepository(bookB.Path).UpdateContentAsync(task.Id, RichContent.FromText("右版本"), task.Revision);
        _cloud.Freeze(); await a.Sync(); await b.Sync(); _cloud.Unfreeze(); await a.Sync(); await b.Sync();
        var conflict = Assert.Single(a.Coordinator.Conflicts); Assert.Equal(2, conflict.Value.Count);
        a.Restart(); Assert.Single(a.Coordinator.Conflicts);
        await a.Coordinator.ResolveAsync(conflict.Key, 0, true, a.Apply); await a.Sync(); await b.Sync();
        var tasks = (await new TaskRepository(bookB.Path).QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items;
        Assert.Equal(new[] { "右版本", "左版本" }.Order(), tasks.Select(t => t.PlainText).Order());
        Assert.Empty(a.Coordinator.Conflicts); Assert.Empty(b.Coordinator.Conflicts);
    }
    [Fact] public async Task PermanentDeletionDoesNotResurrectFromOfflineDevice()
    {
        var (a, b, bookA, bookB, task) = await Pair(); var repo = new TaskRepository(bookA.Path);
        var deleted = await repo.DeleteTaskAsync(task.Id, task.Revision);
        await repo.PermanentlyDeleteAsync(new Dictionary<string, long> { [task.Id] = deleted.Revision });
        await new TaskRepository(bookB.Path).AddTaskAsync(RichContent.FromText("离线新增"), null);
        _cloud.Freeze(); await a.Sync(); await b.Sync(); _cloud.Unfreeze(); await a.Sync(); await b.Sync();
        var tasks = (await new TaskRepository(bookB.Path).QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items;
        Assert.DoesNotContain(tasks, t => t.Id == task.Id); Assert.Single(tasks);
        Assert.Contains((await b.Books.CaptureAsync()).Values, p => CloudSnapshot.Kind(p.Key) == "task" && CloudSnapshot.Id(p.Key) == task.Id && p.Value[0].Deleted);
    }
    [Fact] public async Task DeletionAgainstEditPreservesBothUntilResolved()
    {
        var (a, b, bookA, bookB, task) = await Pair(); var repo = new TaskRepository(bookA.Path);
        var deleted = await repo.DeleteTaskAsync(task.Id, task.Revision); await repo.PermanentlyDeleteAsync(new Dictionary<string, long> { [task.Id] = deleted.Revision });
        await new TaskRepository(bookB.Path).UpdateContentAsync(task.Id, RichContent.FromText("离线修改不能丢"), task.Revision);
        _cloud.Freeze(); await a.Sync(); await b.Sync(); _cloud.Unfreeze(); await a.Sync(); await b.Sync();
        var conflict = a.Coordinator.Conflicts.Single(p => CloudSnapshot.Kind(p.Key) == "task");
        Assert.Contains(conflict.Value, v => v.Deleted); Assert.Contains(conflict.Value, v => !v.Deleted && v.Read<TodoItem>().PlainText == "离线修改不能丢");
    }
    [Fact] public async Task LostCommitResponseResumesSameCommitAfterRestart()
    {
        var a = NewPeer("A"); var book = await a.Library.CreateAsync("Retries", "重试");
        var task = await new TaskRepository(book.Path).AddTaskAsync(RichContent.FromText("初次"), null);
        _cloud.LoseCommitResponse = true;
        await Assert.ThrowsAsync<IOException>(() => a.Sync()); Assert.True(a.Coordinator.HasPending);
        await new TaskRepository(book.Path).UpdateContentAsync(task.Id, RichContent.FromText("等待重试时继续修改"), task.Revision);
        a.Restart(); await a.Sync(); var b = NewPeer("B"); await b.Sync();
        Assert.Equal("等待重试时继续修改", (await TaskById(Assert.Single(await b.Library.ListAsync()), task.Id)).PlainText);
        Assert.False(a.Coordinator.HasPending); Assert.Equal(2, _cloud.CommitCount);
    }
    [Fact] public async Task DownloadDoesNotLoseLocalChangesOnFollowingSync()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        var offline = await new TaskRepository(bookB.Path).AddTaskAsync(RichContent.FromText("只在本地新增"), null);
        await b.Sync(SyncDirection.Download); await b.Sync(); await a.Sync();
        Assert.Equal(offline.PlainText, (await TaskById(bookA, offline.Id)).PlainText);
        Assert.NotNull(await TaskById(bookB, task.Id));
    }
    [Fact] public async Task LateLocalMutationRejectsApplyingStaleSnapshot()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        await new TaskRepository(bookA.Path).UpdateContentAsync(task.Id, RichContent.FromText("云端变更"), task.Revision); await a.Sync();
        var mutated = false;
        await Assert.ThrowsAsync<CloudLocalChangedException>(() => b.Coordinator.SyncAsync(SyncDirection.Both, async (expected, desired, ct) =>
        {
            if (!mutated) { mutated = true; await new TaskRepository(bookB.Path).UpdateContentAsync(task.Id, RichContent.FromText("同步期间修改"), task.Revision); }
            return await b.Apply(expected, desired, ct);
        }));
        Assert.Equal("同步期间修改", (await TaskById(bookB, task.Id)).PlainText);
    }
    [Fact] public async Task ActiveEditingDefersApplyAndDoesNotAcknowledgeRemoteChanges()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        await new TaskRepository(bookA.Path).UpdateContentAsync(task.Id, RichContent.FromText("远端最新"), task.Revision); await a.Sync();
        var result = await b.Coordinator.SyncAsync(SyncDirection.Both, (_, _, _) => Task.FromResult(false));
        Assert.True(result.Deferred); Assert.Equal("初始", (await TaskById(bookB, task.Id)).PlainText);
        await b.Sync(); Assert.Equal("远端最新", (await TaskById(bookB, task.Id)).PlainText);
    }
    [Fact] public async Task CorruptRemoteSnapshotCannotOverwriteLocalDatabase()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        await new TaskRepository(bookA.Path).UpdateContentAsync(task.Id, RichContent.FromText("新远端"), task.Revision); await a.Sync();
        _cloud.CorruptNewestData();
        await Assert.ThrowsAsync<InvalidDataException>(() => b.Sync());
        Assert.Equal("初始", (await TaskById(bookB, task.Id)).PlainText);
    }
    [Fact] public async Task WrongAccountFailsBeforeUploadingAnyData()
    {
        var a = NewPeer("A", new string('b', 64)); await a.Library.CreateAsync("Private", "私有");
        await Assert.ThrowsAsync<InvalidDataException>(() => a.Sync()); Assert.Equal(0, _cloud.Count);
    }
    [Fact] public async Task IndependentlyCreatedSameNamedBooksRemainSeparate()
    {
        var a = NewPeer("A"); var b = NewPeer("B");
        var bookA = await a.Library.CreateAsync("Work", "工作"); var bookB = await b.Library.CreateAsync("Work", "工作");
        await new TaskRepository(bookA.Path).AddTaskAsync(RichContent.FromText("A 的独立笔记"), null);
        await new TaskRepository(bookB.Path).AddTaskAsync(RichContent.FromText("B 的独立笔记"), null);
        _cloud.Freeze(); await a.Sync(); await b.Sync(); _cloud.Unfreeze(); await a.Sync(); await b.Sync();
        Assert.Equal(2, (await a.Library.ListAsync()).Count); Assert.Equal(2, (await b.Library.ListAsync()).Count);
    }
    [Fact] public async Task RemovingModuleAgainstNewAssignedTaskRetainsModuleConflict()
    {
        var (a, b, bookA, bookB, _) = await Pair(); var repoA = new TaskRepository(bookA.Path);
        var project = await repoA.AddProjectAsync("模块"); await a.Sync(); await b.Sync();
        await repoA.DeleteProjectAsync(project.Id!);
        await new TaskRepository(bookB.Path).AddTaskAsync(RichContent.FromText("离线新增关联"), project.Id);
        _cloud.Freeze(); await a.Sync(); await b.Sync(); _cloud.Unfreeze(); await a.Sync(); await b.Sync();
        Assert.Contains(a.Coordinator.Conflicts, p => CloudSnapshot.Kind(p.Key) == "project");
        using var c = Database.Open(bookA.Path); BookLibrary.Validate(c, bookA.Path);
    }
    [Fact] public async Task DeletedBookAgainstChangedTaskRetainsBookConflict()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        await a.Library.DeleteAsync(bookA);
        await new TaskRepository(bookB.Path).UpdateContentAsync(task.Id, RichContent.FromText("删除期间的修改"), task.Revision);
        _cloud.Freeze(); await a.Sync(); await b.Sync(); _cloud.Unfreeze(); await a.Sync(); await b.Sync();
        Assert.Contains(a.Coordinator.Conflicts, p => CloudSnapshot.Kind(p.Key) == "book");
        Assert.NotEmpty(await a.Library.ListAsync());
    }
    [Theory] [InlineData(1)] [InlineData(127)] [InlineData(128)] [InlineData(129)] [InlineData(524288)] [InlineData(524289)] [InlineData(1048576)]
    public void UploadHashesCoverCheckAndBlockBoundaries(int size)
    {
        var data = Enumerable.Range(0, size).Select(i => (byte)(i * 17 + 9)).ToArray(); var hash = WeiyunHash.Compute(data);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(data)), hash.FileSha);
        Assert.Equal(hash.FileSha, hash.BlockShaList[^1]); Assert.Equal((size + WeiyunHash.BlockSize - 1) / WeiyunHash.BlockSize, hash.BlockShaList.Count);
        var tail = Convert.FromBase64String(hash.CheckData); Assert.Equal(data[^tail.Length..], tail);
        if (size <= 128) Assert.Equal("0123456789abcdeffedcba9876543210f0e1d2c3", hash.CheckSha);
    }
    [Fact] public async Task BoundedReadsRejectOverSizedObjects()
    {
        using var stream = new MemoryStream(new byte[1025]);
        await Assert.ThrowsAsync<InvalidDataException>(() => WeiyunFileStore.ReadBoundedAsync(stream, 1024, CancellationToken.None));
    }
    [Fact] public void WindowsCredentialsAreEncryptedAndWorkspaceIdsCannotEscape()
    {
        if (!OperatingSystem.IsWindows()) return;
        var text = Encoding.UTF8.GetBytes("test-only-access-token"); var encrypted = WindowsCloudCredentials.Protect(text);
        Assert.NotEqual(text, encrypted); Assert.Equal(text, WindowsCloudCredentials.Unprotect(encrypted));
        Assert.Throws<InvalidDataException>(() => WindowsCloudCredentials.Workspace(_root, "../other"));
    }
    [Fact] public async Task AppearanceAndLoginCredentialsAreExcludedFromCloudPayload()
    {
        var a = NewPeer("A"); await a.Library.CreateAsync("Notes", "笔记"); a.Library.SaveSettings(new() { ThemeId = "secret-theme", CloudAutomaticSync = false });
        var payload = CloudJson.Serialize(await a.Books.CaptureAsync()); Assert.DoesNotContain("secret-theme", payload); Assert.DoesNotContain("access_token", payload);
    }
    [Fact] public async Task CloudApplyDoesNotReduceLocalRevisionsOrRepeatedlyBackUpUnchangedTasks()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        await new TaskRepository(bookA.Path).UpdateContentAsync(task.Id, RichContent.FromText("一次远端修改"), task.Revision); await a.Sync(); await b.Sync();
        var revision = (await TaskById(bookB, task.Id)).Revision;
        var backups = Directory.EnumerateFiles(b.Library.BackupDirectory, "*.db").Count();
        await b.Sync(); await b.Sync();
        Assert.Equal(revision, (await TaskById(bookB, task.Id)).Revision); Assert.Equal(backups, Directory.EnumerateFiles(b.Library.BackupDirectory, "*.db").Count());
    }
    [Fact] public async Task UnopposedBookDeletionPropagatesWithoutRestoringOldChildren()
    {
        var (a, b, bookA, _, _) = await Pair(); await a.Library.DeleteAsync(bookA); await a.Sync(); await b.Sync();
        Assert.Empty(await b.Library.ListAsync()); b.Restart(); await b.Sync(); Assert.Empty(await b.Library.ListAsync());
    }
    [Fact] public async Task ChoosingModuleDeletionMovesNewDependentTasksToUnassigned()
    {
        var (a, b, bookA, bookB, _) = await Pair(); var repoA = new TaskRepository(bookA.Path);
        var project = await repoA.AddProjectAsync("待删除模块"); await a.Sync(); await b.Sync();
        await repoA.DeleteProjectAsync(project.Id!); var task = await new TaskRepository(bookB.Path).AddTaskAsync(RichContent.FromText("关联任务必须保留"), project.Id);
        _cloud.Freeze(); await a.Sync(); await b.Sync(); _cloud.Unfreeze(); await a.Sync();
        var conflict = a.Coordinator.Conflicts.Single(p => CloudSnapshot.Kind(p.Key) == "project");
        var deleteIndex = conflict.Value.FindIndex(v => v.Deleted);
        await a.Coordinator.ResolveAsync(conflict.Key, deleteIndex, false, a.Apply); await a.Sync(); await b.Sync();
        Assert.Null((await TaskById(bookB, task.Id)).ProjectId); Assert.Empty(b.Coordinator.Conflicts);
    }
    [Fact] public async Task ManualReplacementKeepsIdentityAndMarksRemovedRows()
    {
        var a = NewPeer("A"); var source = NewPeer("Source");
        var target = await a.Library.CreateAsync("Same", "目标"); var original = await new TaskRepository(target.Path).AddTaskAsync(RichContent.FromText("旧任务"), null);
        var incoming = await source.Library.CreateAsync("Same", "导入"); await new TaskRepository(incoming.Path).AddTaskAsync(RichContent.FromText("新任务"), null);
        var before = await a.Books.CaptureAsync(); var bookId = CloudSnapshot.Book(before.Values.Keys.First());
        using var prepared = await a.Library.PrepareImportAsync(incoming.Path); await a.Library.ImportAsync(prepared, target, ImportMode.Replace);
        var after = await a.Books.CaptureAsync(); Assert.True(after.Values.ContainsKey(CloudSnapshot.Key(bookId, "book", bookId)));
        Assert.True(after.Values[CloudSnapshot.Key(bookId, "task", original.Id)][0].Deleted);
    }
    [Fact] public async Task RetainedClockSkewDoesNotSelectWinnerForDivergentTaskContent()
    {
        var (a, b, bookA, bookB, task) = await Pair();
        await new TaskRepository(bookA.Path).UpdateContentAsync(task.Id, RichContent.FromText("本机时钟快"), task.Revision);
        await new TaskRepository(bookB.Path).UpdateContentAsync(task.Id, RichContent.FromText("本机时钟慢"), task.Revision);
        await Database.RunAsync(bookA.Path, () => { using var c = Database.Open(bookA.Path); Database.Exec(c, "UPDATE Tasks SET UpdatedAt=$now,EditedAt=$now WHERE Id=$id", ("$now", task.CreatedAt + 86400000), ("$id", task.Id)); return true; });
        await a.Sync(); await b.Sync(); Assert.Contains(b.Coordinator.Conflicts, p => CloudSnapshot.Kind(p.Key) == "task" && p.Value.Count == 2);
    }
    private sealed class Peer
    {
        public BookLibrary Library { get; }
        public CloudBookStorage Books { get; }
        public SyncCoordinator Coordinator { get; private set; }
        private readonly FakeCloud _cloud; private readonly string _account;
        public Peer(BookLibrary library, FakeCloud cloud, string account) { Library = library; Books = new(library); _cloud = cloud; _account = account; Coordinator = new(cloud, Books, account); }
        public async Task<bool> Apply(CloudSnapshot expected, CloudSnapshot desired, CancellationToken ct) { await Books.ApplyAsync(expected, desired, ct); return true; }
        public Task<CloudSyncResult> Sync(SyncDirection direction = SyncDirection.Both) => Coordinator.SyncAsync(direction, Apply);
        public void Restart() => Coordinator = new(_cloud, Books, _account);
    }
    private sealed class FakeCloud : ICloudFileStore
    {
        public static string Account => new('a', 64);
        private readonly Dictionary<(CloudArea Area, string Name), byte[]> _files = [];
        private HashSet<string>? _frozen;
        public int ReadCalls, WriteCalls, ListCalls, InitializeCalls;
        public CloudTransportException? InitializeFailure;
        public void ResetCalls() { ReadCalls = WriteCalls = ListCalls = InitializeCalls = 0; }
        public bool LoseCommitResponse;
        public bool LoseProtocolResponse;
        public int Count => _files.Count;
        public int CommitCount => _files.Count(p => p.Key.Area == CloudArea.Data);
        public void Freeze() => _frozen = _files.Keys.Select(k => k.Name).ToHashSet();
        public void Unfreeze() => _frozen = null;
        public Task<CloudSpace> InitializeAsync(CancellationToken ct = default) { InitializeCalls++; if (InitializeFailure != null) throw InitializeFailure; return Task.FromResult(new CloudSpace(Account, "root", "Data", "Sync")); }
        public Task<IReadOnlyList<CloudFile>> ListAsync(CloudArea area, CancellationToken ct = default)
        { ListCalls++; return Task.FromResult<IReadOnlyList<CloudFile>>(_files.Where(p => p.Key.Area == area && (_frozen == null || _frozen.Contains(p.Key.Name) || CloudDbCodec.ProtocolName.IsMatch(p.Key.Name))).Select(p => new CloudFile(p.Key.Name, p.Key.Name, p.Value.Length)).ToArray()); }
        public Task<byte[]> ReadAsync(CloudArea area, CloudFile file, CancellationToken ct = default) { ReadCalls++; return Task.FromResult(_files[(area, file.Name)].ToArray()); }
        public Task<CloudFile> WriteImmutableAsync(CloudArea area, string name, byte[] bytes, CancellationToken ct = default)
        {
            WriteCalls++;
            ct.ThrowIfCancellationRequested();
            if (_files.TryGetValue((area, name), out var old) && !old.SequenceEqual(bytes)) throw new InvalidDataException("Attempted mutable overwrite");
            _files[(area, name)] = bytes.ToArray();
            if (CloudDbCodec.ProtocolName.IsMatch(name) && LoseProtocolResponse) { LoseProtocolResponse = false; throw new IOException("Protocol response lost"); }
            if (area == CloudArea.Data && LoseCommitResponse) { LoseCommitResponse = false; throw new IOException("Response lost after successful DB upload"); }
            return Task.FromResult(new CloudFile(name, name, bytes.Length));
        }
        public void CorruptNewestData() { var key = _files.Keys.Last(k => k.Area == CloudArea.Data); _files[key] = [1, 2, 3]; }
        public bool FailCleanup;
        public Task RecycleAsync(CloudArea area, IReadOnlyList<CloudFile> files, CancellationToken ct = default)
        {
            if (FailCleanup) throw new IOException("Synthetic deletion permission denied");
            foreach (var file in files) _files.Remove((area, file.Name));
            return Task.CompletedTask;
        }
    }
}
