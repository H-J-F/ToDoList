using System.Net;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed class CloudRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "todo-repairs-" + Guid.NewGuid().ToString("N"));
    private BookLibrary Library => new(Path.Combine(_root, "Data"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Tokens : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"new\",\"refresh_token\":\"rotated\",\"expires_in\":3600}") }); }
    }
    [Fact] public async Task FourteenDayBoundaryRejectsEvenFreshAccessTokenAndRefreshDoesNotExtendAuthorization()
    {
        var clock = new Clock(new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        var start = clock.Now.ToUnixTimeSeconds(); var saved = new List<CloudSession>();
        using var handler = new Tokens(); using var http = new HttpClient(handler);
        using var auth = new WeiyunAuthProvider(_ => { }, saved.Add, new("c", "a", "r", long.MaxValue, "root", start), http, timeProvider: clock);
        clock.Now = clock.Now.AddDays(14).AddSeconds(-1);
        Assert.Equal("a", await auth.GetAccessTokenAsync());
        Assert.Equal("new", await auth.GetAccessTokenAsync(true)); Assert.Equal(start, auth.Session!.AuthorizedAt);
        clock.Now = clock.Now.AddSeconds(1);
        await Assert.ThrowsAsync<CloudAuthenticationException>(() => auth.GetAccessTokenAsync());
        await Assert.ThrowsAsync<CloudAuthenticationException>(() => auth.GetAccessTokenAsync(true)); Assert.Equal(1, handler.Requests);
    }
    [Fact] public async Task OldSessionGetsOnlyOneGracePeriodAcrossRestartAndRefresh()
    {
        var clock = new Clock(new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero)); CloudSession? stored = null;
        using var handler = new Tokens(); using var http = new HttpClient(handler);
        using (var auth = new WeiyunAuthProvider(_ => { }, s => stored = s, new("c", "a", "r", long.MaxValue, "root"), http, timeProvider: clock))
        { await auth.GetAccessTokenAsync(); Assert.Equal(clock.Now.ToUnixTimeSeconds(), stored!.AuthorizedAt); }
        var start = stored!.AuthorizedAt; clock.Now = clock.Now.AddDays(13);
        using var restarted = new WeiyunAuthProvider(_ => { }, s => stored = s, stored, http, timeProvider: clock);
        await restarted.GetAccessTokenAsync(true); Assert.Equal(start, stored!.AuthorizedAt);
        clock.Now = clock.Now.AddDays(1); await Assert.ThrowsAsync<CloudAuthenticationException>(() => restarted.GetAccessTokenAsync());
    }
    [Theory]
    [InlineData(5)] [InlineData(10)] [InlineData(15)] [InlineData(20)] [InlineData(25)] [InlineData(30)] [InlineData(60)]
    public void ChangesNeverAdvanceAutomaticCycle(int minutes)
    {
        var now = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc); var schedule = new CloudSyncSchedule();
        Assert.True(schedule.Due(now, true)); Assert.False(schedule.Due(now, false));
        schedule.Completed(now, minutes, schedule.Generation, false);
        schedule.Changed(now); Assert.False(schedule.Due(now.AddSeconds(4), true)); Assert.False(schedule.Due(now.AddSeconds(6), true));
        schedule.Completed(now.AddSeconds(7), minutes, now, false);
        Assert.Null(schedule.DirtyAt); Assert.Equal(now.AddSeconds(7).AddMinutes(minutes), schedule.NextPoll);
        Assert.False(schedule.Due(schedule.NextPoll.AddTicks(-1), true)); Assert.True(schedule.Due(schedule.NextPoll, true));
        schedule.Changed(now.AddSeconds(10)); schedule.Completed(now.AddSeconds(16), minutes, now.AddSeconds(10), false);
        Assert.Equal(now.AddSeconds(16).AddMinutes(minutes), schedule.NextPoll);
    }
    [Fact] public void DeferredEditingAndFailuresWaitForNextCycle()
    {
        var now = DateTime.UtcNow; var schedule = new CloudSyncSchedule();
        schedule.Completed(now, 5, null, true); Assert.False(schedule.Due(now.AddSeconds(6), true));
        schedule.EditingEnded(now); Assert.False(schedule.Due(now, true)); Assert.True(schedule.Due(now.AddMinutes(5), true));
        foreach (var seconds in new[] { 5, 15, 45, 135, 300, 300 })
        { schedule.Failed(now); Assert.Equal(now.AddSeconds(seconds), schedule.NextAttempt); Assert.False(schedule.Due(now, true)); }
        Assert.Equal(5, CloudSyncSchedule.Normalize(4)); Assert.Equal(new[] { 5, 10, 15, 20, 25, 30, 60 }, CloudSyncSchedule.Intervals);
    }
    [Theory]
    [InlineData(2, 5)] [InlineData(3, 5)] [InlineData(8, 5)] [InlineData(0, 5)] [InlineData(4, 5)]
    [InlineData(5, 5)] [InlineData(10, 10)] [InlineData(15, 15)] [InlineData(20, 20)]
    [InlineData(25, 25)] [InlineData(30, 30)] [InlineData(60, 60)]
    public void IntervalSettingsNormalizeAndSurviveReload(int stored, int expected)
    {
        Library.SaveSettings(new AppSettings { CloudSyncIntervalMinutes = stored });
        var settings = Library.LoadSettings(); Assert.Equal(expected, settings.CloudSyncIntervalMinutes);
        Library.SaveSettings(settings);
        Assert.Equal(expected, new BookLibrary(Library.DataDirectory).LoadSettings().CloudSyncIntervalMinutes);
    }
    [Fact] public async Task RenameAndRepeatedImportKeepIdentityAndIndependentChanges()
    {
        var guest = new BookLibrary(Path.Combine(_root, "Guest")); var book = await guest.CreateAsync("Book", "原名");
        var sourceRepo = new TaskRepository(book.Path); var first = await sourceRepo.AddTaskAsync(RichContent.FromText("原内容"), null);
        var imported = await Library.CopyBookAsync(guest, book); Assert.Equal(book.BookId, imported.BookId);
        var renamed = await BookOperations.RenameAsync(Library, imported, "云端新名");
        Assert.Equal(imported.Path, renamed.Path); Assert.Equal(imported.BookId, renamed.BookId);
        await new TaskRepository(imported.Path).AddTaskAsync(RichContent.FromText("目标独有"), null);
        await sourceRepo.AddTaskAsync(RichContent.FromText("源独有"), null);
        await Library.CopyBookAsync(guest, book); var before = await new CloudBookStorage(Library).CaptureAsync();
        await Library.CopyBookAsync(guest, book); var after = await new CloudBookStorage(Library).CaptureAsync();
        Assert.Equal(CloudJson.Serialize(before), CloudJson.Serialize(after)); Assert.Single(await Library.ListAsync());
        Assert.Equal("云端新名", (await Library.ListAsync())[0].Title);
        Assert.Equal(3, after.Values.Count(p => CloudSnapshot.Kind(p.Key) == "task"));
        Assert.Equal("原名", (await guest.ListAsync())[0].Title); Assert.Equal(first.Id, (await sourceRepo.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items[0].Id);
    }
    [Fact] public async Task ImportConflictsSurviveRestartAndCanBeResolvedWithoutNetwork()
    {
        var guest = new BookLibrary(Path.Combine(_root, "Guest")); var book = await guest.CreateAsync("Book", "笔记");
        var original = await new TaskRepository(book.Path).AddTaskAsync(RichContent.FromText("原内容"), null);
        var imported = await Library.CopyBookAsync(guest, book); var target = new TaskRepository(imported.Path);
        await target.UpdateContentAsync(original.Id, RichContent.FromText("目标修改"), original.Revision);
        await new TaskRepository(book.Path).UpdateContentAsync(original.Id, RichContent.FromText("源修改"), original.Revision);
        await Library.CopyBookAsync(guest, book); await Library.CopyBookAsync(guest, book);
        var storage = new CloudBookStorage(Library); var sync = new SyncCoordinator(new OfflineFiles(), storage, new string('0', 64));
        var key = CloudSnapshot.Key(book.BookId, "task", original.Id); Assert.Equal(2, sync.Conflicts[key].Count);
        var index = sync.Conflicts[key].FindIndex(v => v.Read<TodoItem>().PlainText == "源修改");
        await sync.ResolveAsync(key, index, false, async (a, b, ct) => { await storage.ApplyAsync(a, b, ct); return true; });
        Assert.Empty(new SyncCoordinator(new OfflineFiles(), storage, new string('0', 64)).Conflicts);
        Assert.Equal("源修改", Assert.Single((await target.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items).PlainText);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ModuleMigrationTransfersRecycleBinHistoryAndTombstones(bool merge)
    {
        var source = await Library.CreateAsync("Source", "源"); var target = await Library.CreateAsync("Target", "目标");
        var from = new TaskRepository(source.Path); var to = new TaskRepository(target.Path);
        var project = await from.AddProjectAsync("模块"); var existing = merge ? await to.AddProjectAsync("模块") : null;
        var task = await from.AddTaskAsync(RichContent.FromText("富文本"), project.Id);
        task = await from.SetStatusAsync(task.Id, TodoStatus.Completed, task.Revision);
        var deleted = await from.AddTaskAsync(RichContent.FromText("回收站"), project.Id); await from.DeleteTaskAsync(deleted.Id, deleted.Revision);
        var changes = await BookOperations.MigrateProjectAsync(Library, source, target, project.Id!, mergeProjectId: existing?.Id);
        Assert.True(changes.Affects(source.BookId)); Assert.True(changes.Affects(target.BookId));
        Assert.Empty(await from.GetProjectsAsync()); Assert.Empty((await from.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items);
        Assert.Empty((await from.QueryAsync(new(null, TaskFilter.Deleted, TaskSort.Deleted))).Items);
        Assert.Single(await to.GetProjectsAsync());
        var transferred = Assert.Single((await to.QueryAsync(new(null, TaskFilter.Completed, TaskSort.Completed))).Items);
        Assert.Equal(task.Id, transferred.Id); Assert.Equal(task.ContentJson, transferred.ContentJson); Assert.Equal(existing?.Id ?? project.Id, transferred.ProjectId);
        Assert.Equal(deleted.Id, Assert.Single((await to.QueryAsync(new(null, TaskFilter.Deleted, TaskSort.Deleted))).Items).Id);
        using var c = Database.Open(source.Path, true); Assert.Equal(5L, Database.Scalar(c, "SELECT COUNT(*) FROM SyncTombstones"));
        using var destination = Database.Open(target.Path, true); Assert.Equal(2L, Database.Scalar(destination, "SELECT COUNT(*) FROM TaskStateEvents"));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ModuleCanMigrateBackRepeatedlyAndClearsDestinationTombstones(bool merge)
    {
        var source = await Library.CreateAsync("RoundTripSource", "源"); var target = await Library.CreateAsync("RoundTripTarget", "目标");
        var from = new TaskRepository(source.Path); var to = new TaskRepository(target.Path);
        var project = await from.AddProjectAsync("模块"); var same = merge ? await to.AddProjectAsync("模块") : null;
        var task = await from.AddTaskAsync(RichContent.FromText("往返内容"), project.Id); task = await from.SetStatusAsync(task.Id, TodoStatus.Completed, task.Revision);
        var deleted = await from.AddTaskAsync(RichContent.FromText("回收站"), project.Id); await from.DeleteTaskAsync(deleted.Id, deleted.Revision);
        await BookOperations.MigrateProjectAsync(Library, source, target, project.Id!, mergeProjectId: same?.Id);
        var id = same?.Id ?? project.Id!;
        for (var i = 0; i < 3; i++)
        {
            await BookOperations.MigrateProjectAsync(Library, target, source, id!);
            Assert.Equal(task.Id, (await from.QueryAsync(new(null, TaskFilter.Completed, TaskSort.Completed))).Items.Single().Id);
            Assert.Equal(deleted.Id, (await from.QueryAsync(new(null, TaskFilter.Deleted, TaskSort.Deleted))).Items.Single().Id);
            using (var c = Database.Open(source.Path, true))
            { Assert.Equal(0L, Database.Scalar(c, "SELECT COUNT(*) FROM SyncTombstones WHERE EntityId=$id", ("$id", task.Id))); Assert.Equal(2L, Database.Scalar(c, "SELECT COUNT(*) FROM TaskStateEvents")); }
            await BookOperations.MigrateProjectAsync(Library, source, target, id!);
        }
    }
    [Fact] public async Task FailedSecondDatabaseReplacementRollsBackBothAndNoopHasNoBackup()
    {
        var source = await Library.CreateAsync("Source", "源"); var target = await Library.CreateAsync("Target", "目标");
        var from = new TaskRepository(source.Path); var project = await from.AddProjectAsync("模块"); await from.AddTaskAsync(RichContent.FromText("保留"), project.Id);
        var storage = new CloudBookStorage(Library); var before = await storage.CaptureAsync();
        Assert.False((await storage.ApplyWithChangesAsync(before, before)).HasChanges); Assert.False(Directory.Exists(Library.BackupDirectory));
        await Assert.ThrowsAsync<IOException>(() => BookOperations.MigrateProjectAsync(Library, source, target, project.Id!, beforeReplace: i => { if (i == 1) throw new IOException("simulated interruption"); }));
        Assert.Equal(CloudJson.Serialize(before), CloudJson.Serialize(await storage.CaptureAsync()));
        Assert.False(File.Exists(Path.Combine(storage.StateDirectory, "apply-journal.json")));
    }
    [Fact] public async Task InvalidNameAndRecordCollisionLeaveBothBooksIntact()
    {
        var source = await Library.CreateAsync("Source", "源"); var target = await Library.CreateAsync("Target", "目标");
        var from = new TaskRepository(source.Path); var to = new TaskRepository(target.Path); var p = await from.AddProjectAsync("模块"); var q = await to.AddProjectAsync("模块");
        await from.AddTaskAsync(RichContent.FromText("源内容"), p.Id);
        var before = await new CloudBookStorage(Library).CaptureAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BookOperations.MigrateProjectAsync(Library, source, target, p.Id!));
        await BookOperations.MigrateProjectAsync(Library, source, target, p.Id!, "改名");
        Assert.Equal(new[] { "模块", "改名" }, (await to.GetProjectsAsync()).Select(x => x.Name));
        Assert.True(CloudBookStorage.PreviewChanges(before, await new CloudBookStorage(Library).CaptureAsync()).HasChanges);
    }
    [Fact] public async Task SameTaskIdWithDifferentContentRejectsMigrationBeforeChangingEitherBook()
    {
        var source = await Library.CreateAsync("Source", "源"); var target = await Library.CreateAsync("Target", "目标");
        var from = new TaskRepository(source.Path); var to = new TaskRepository(target.Path);
        var p = await from.AddProjectAsync("模块"); var q = await to.AddProjectAsync("模块");
        var first = await from.AddTaskAsync(RichContent.FromText("源内容"), p.Id);
        var second = await to.AddTaskAsync(RichContent.FromText("不同内容"), q.Id);
        using (var c = Database.Open(target.Path)) Database.Exec(c, "UPDATE Tasks SET Id=$id WHERE Id=$old", ("$id", first.Id), ("$old", second.Id));
        var storage = new CloudBookStorage(Library); var before = await storage.CaptureAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => BookOperations.MigrateProjectAsync(Library, source, target, p.Id!, mergeProjectId: q.Id));
        Assert.Equal(CloudJson.Serialize(before), CloudJson.Serialize(await storage.CaptureAsync()));
        Assert.False(Directory.Exists(Library.BackupDirectory));
    }
    [Fact] public async Task MigrationHonorsConflictGuardAndCancelledOperation()
    {
        var source = await Library.CreateAsync("Source", "源"); var target = await Library.CreateAsync("Target", "目标");
        var from = new TaskRepository(source.Path); var p = await from.AddProjectAsync("模块"); var task = await from.AddTaskAsync(RichContent.FromText("源内容"), p.Id);
        var storage = new CloudBookStorage(Library); var before = await storage.CaptureAsync();
        var key = CloudSnapshot.Key(source.BookId, "task", task.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => BookOperations.MigrateProjectAsync(Library, source, target, p.Id!, conflicts: new Dictionary<string, List<CloudValue>> { [key] = before.Values[key] }));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BookOperations.MigrateProjectAsync(Library, source, target, p.Id!, ct: cancelled.Token));
        Assert.Equal(CloudJson.Serialize(before), CloudJson.Serialize(await storage.CaptureAsync()));
    }
    [Fact] public async Task ReplaceAfterInternalFilenameCollisionKeepsTargetPathAndIdentity()
    {
        var guest = new BookLibrary(Path.Combine(_root, "Guest")); var source = await guest.CreateAsync("Same", "导入");
        await Library.CreateAsync("Same", "已有不同 ID"); var imported = await Library.CopyBookAsync(guest, source);
        Assert.NotEqual(source.Name, imported.Name);
        var file = Path.Combine(_root, "export.db"); await guest.ExportAsync(source, file);
        using var prepared = await Library.PrepareImportAsync(file);
        var replaced = await Library.ImportAsync(prepared, imported, ImportMode.Replace);
        Assert.Equal(imported.Path, replaced.Path); Assert.Equal(source.BookId, replaced.BookId);
        Assert.Equal(2, (await Library.ListAsync()).Count); Assert.Null(Library.ScanWarning);
    }
    private sealed class OfflineFiles : ICloudFileStore
    {
        public Task<CloudSpace> InitializeAsync(CancellationToken ct = default) => throw new InvalidOperationException("Network must not be used");
        public Task<IReadOnlyList<CloudFile>> ListAsync(CloudArea area, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> ReadAsync(CloudArea area, CloudFile file, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CloudFile> WriteImmutableAsync(CloudArea area, string name, byte[] bytes, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
