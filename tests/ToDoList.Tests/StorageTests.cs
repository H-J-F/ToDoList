using Microsoft.Data.Sqlite;
using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ToDoList-tests-" + Guid.NewGuid().ToString("N"));
    private BookLibrary Library => new(Path.Combine(_root, "Data"));
    private async Task<(BookInfo Book, TaskRepository Repo)> Create(string name = "Work")
    { var book = await Library.CreateAsync(name, "工作手账"); return (book, new(book.Path)); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact] public async Task FirstLaunchHasNoBookAndCaseInsensitiveNames()
    {
        Assert.Empty(await Library.ListAsync()); await Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Library.CreateAsync("work", "重复"));
        Assert.Single(await Library.ListAsync());
    }
    [Theory]
    [InlineData("")][InlineData("Work 1")][InlineData("Book1\n")][InlineData("Book1\t")][InlineData("COM1")][InlineData("lpt9")][InlineData("Book_1")][InlineData("../Book")][InlineData("我的书")][InlineData("CON")][InlineData("aux")]
    public void InvalidBookNamesAreRejected(string name) => Assert.Throws<ArgumentException>(() => BookRules.ValidateName(name));

    [Fact] public async Task ProjectAndTaskAreAtomicAndAllMeansUnassigned()
    {
        var (_, repo) = await Create();
        await repo.AddTaskAsync(RichContent.FromText("买一本书 📚"), null, "读书");
        var project = Assert.Single(await repo.GetProjectsAsync());
        var list = await repo.QueryAsync(new(project.Id, TaskFilter.Open, TaskSort.Created)); Assert.Single(list.Items);
        await repo.AddTaskAsync(RichContent.FromText("散步"), null);
        Assert.Equal(2, (await repo.QueryAsync(new(null, TaskFilter.Open, TaskSort.Created))).Items.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.AddTaskAsync(RichContent.FromText("  "), null, "不应创建"));
        Assert.Single(await repo.GetProjectsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.AddProjectAsync("读书"));
    }
    [Fact] public async Task ProjectsCanBeRenamedReorderedAndDeletedWithoutDeletingTasks()
    {
        var (_, repo) = await Create();
        var first = await repo.AddProjectAsync("第一"); var second = await repo.AddProjectAsync("第二");
        var task = await repo.AddTaskAsync(RichContent.FromText("保留任务"), first.Id);
        await repo.ReorderProjectsAsync([second.Id!, first.Id!]);
        Assert.Equal([second.Id, first.Id], (await repo.GetProjectsAsync()).Select(p => p.Id));
        var renamed = await repo.RenameProjectAsync(first.Id!, "已重命名"); Assert.Equal("已重命名", renamed.Name);
        await repo.DeleteProjectAsync(first.Id!);
        Assert.DoesNotContain(await repo.GetProjectsAsync(), p => p.Id == first.Id);
        Assert.Null(Assert.Single((await repo.QueryAsync(new(null, TaskFilter.Open, TaskSort.Created))).Items, p => p.Id == task.Id).ProjectId);
    }
    [Fact] public async Task BookDeletionCreatesBackupAndRemovesManagedDatabase()
    {
        var (book, _) = await Create();
        var backup = await Library.DeleteAsync(book);
        Assert.False(File.Exists(book.Path)); Assert.True(File.Exists(backup)); Assert.StartsWith(Library.BackupDirectory, backup, StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task StatusColorsAreStoredInBookAndUsedByReports()
    {
        var (_, repo) = await Create(); await repo.SetSettingAsync("StatusColor.Open", "#123456"); await repo.AddTaskAsync(RichContent.FromText("颜色"), null);
        var options = new ReportOptions(null, TaskFilter.All, DateSelection.Any, ReportFormat.Markdown);
        var report = TaskReport.Create(await repo.ReadReportAsync(options), options, DateTimeOffset.Now);
        Assert.Equal("#123456", report.ResolveColor(ReportColor.Open)); Assert.Contains("#123456", report.ToMarkdown());
    }
    [Fact] public async Task CompletionReversalAndVerificationPreserveHistory()
    {
        var (book, repo) = await Create(); var task = await repo.AddTaskAsync(RichContent.FromText("任务"), null);
        task = await repo.SetStatusAsync(task.Id, TodoStatus.Verification, task.Revision);
        Assert.Single((await repo.QueryAsync(new(null, TaskFilter.Open, TaskSort.Created))).Items);
        task = await repo.SetStatusAsync(task.Id, TodoStatus.Completed, task.Revision); var firstCompleted = task.CompletedAt;
        Assert.NotNull(firstCompleted); Assert.Empty((await repo.QueryAsync(new(null, TaskFilter.Open, TaskSort.Created))).Items);
        task = await repo.SetStatusAsync(task.Id, TodoStatus.Open, task.Revision); Assert.Null(task.CompletedAt);
        Assert.Empty((await repo.QueryAsync(new(null, TaskFilter.Completed, TaskSort.Completed))).Items);
        task = await repo.SetStatusAsync(task.Id, TodoStatus.Completed, task.Revision); Assert.True(task.CompletedAt > firstCompleted);
        using var c = Database.Open(book.Path); Assert.Equal(4L, Database.Scalar(c, "SELECT COUNT(*) FROM TaskStateEvents"));
    }
    [Fact] public async Task ContentEditCannotChangeProjectOrCompletionAndRejectsStaleRevision()
    {
        var (_, repo) = await Create(); var task = await repo.AddTaskAsync(RichContent.FromText("前"), null, "工作");
        var completed = await repo.SetStatusAsync(task.Id, TodoStatus.Completed, task.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.UpdateContentAsync(task.Id, RichContent.FromText("过期"), task.Revision));
        var edited = await repo.UpdateContentAsync(task.Id, RichContent.FromText("后 🌱"), completed.Revision);
        Assert.Equal(task.ProjectId, edited.ProjectId); Assert.Equal(completed.CompletedAt, edited.CompletedAt);
    }
    [Fact] public async Task ConcurrentWritesCommitExactlyOncePerTask()
    {
        var (book, repo) = await Create();
        var tasks = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => repo.AddTaskAsync(RichContent.FromText($"任务{i}"), null)));
        await Task.WhenAll(tasks.Select(t => repo.SetStatusAsync(t.Id, TodoStatus.Completed, t.Revision)));
        Assert.Equal(20, (await repo.QueryAsync(new(null, TaskFilter.Completed, TaskSort.Completed))).Items.Count);
        using var c = Database.Open(book.Path); Assert.Equal(20L, Database.Scalar(c, "SELECT COUNT(*) FROM TaskStateEvents"));
    }
    [Fact] public async Task KeysetPagesHaveNoDuplicatesAndCanGoBackwardsOnEqualTimestamps()
    {
        var (book, repo) = await Create(); Seed(book.Path, 550);
        var query = new TaskQuery(null, TaskFilter.Open, TaskSort.Created);
        var a = await repo.QueryAsync(query); var b = await repo.QueryAsync(query with { Cursor = query.CursorFor(a.Items[0]) });
        var back = await repo.QueryAsync(query with { Cursor = query.CursorFor(b.Items[^1]), Direction = PageDirection.Newer });
        Assert.Equal(200, a.Items.Count); Assert.True(a.HasMore);
        Assert.Equal(a.Items.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id, StringComparer.Ordinal).Select(t => t.Id), a.Items.Select(t => t.Id));
        Assert.Empty(a.Items.Select(t => t.Id).Intersect(b.Items.Select(t => t.Id)));
        Assert.Equal(a.Items.Select(t => t.Id), back.Items.Select(t => t.Id)); Assert.False(back.HasMore);
    }
    [Fact] public async Task LatestWindowIsAscendingAndInclusiveRefreshDoesNotDropAnchor()
    {
        var (book, repo) = await Create(); Seed(book.Path, 550);
        var query = new TaskQuery(null, TaskFilter.Open, TaskSort.Created);
        var page = await repo.QueryAsync(query);
        using var connection = Database.Open(book.Path);
        using var command = Database.Command(connection, "SELECT Id FROM Tasks WHERE Status IN (0,1) ORDER BY CreatedAt DESC,Id DESC LIMIT 200");
        using var reader = command.ExecuteReader(); var expected = new List<string>(); while (reader.Read()) expected.Add(reader.GetString(0)); expected.Reverse();
        Assert.Equal(expected, page.Items.Select(t => t.Id));
        var refreshed = await repo.QueryAsync(query with { Cursor = query.CursorFor(page.Items[0]), Direction = PageDirection.Newer, IncludeCursor = true });
        Assert.Equal(expected, refreshed.Items.Select(t => t.Id));
        Assert.False(refreshed.HasMore);
    }
    [Fact] public async Task AllQueryShapesUseIndexesWithoutTemporarySort()
    {
        var (book, _) = await Create(); Seed(book.Path, 1000);
        using var c = Database.Open(book.Path);
        foreach (var filter in Enum.GetValues<TaskFilter>()) foreach (var project in new string?[] { null, "project" })
        foreach (var sort in Enum.GetValues<TaskSort>())
        {
            var q = new TaskQuery(project, filter, sort, filter is TaskFilter.Today or TaskFilter.Month or TaskFilter.Week ? 0 : null,
                filter is TaskFilter.Today or TaskFilter.Month or TaskFilter.Week ? long.MaxValue : null);
            using var cmd = TaskRepository.BuildQuery(c, q, true); using var reader = cmd.ExecuteReader();
            var plan = new List<string>(); while (reader.Read()) plan.Add(reader.GetString(3));
            Assert.DoesNotContain(plan, p => p.Contains("TEMP B-TREE")); Assert.Contains(plan, p => p.Contains("USING INDEX"));
        }
    }
    [Fact] public async Task ExportIncludesUncheckpointedWalAndImportsByInternalName()
    {
        var (book, repo) = await Create();
        using var held = Database.Open(book.Path); Database.Exec(held, "PRAGMA wal_autocheckpoint=0");
        await repo.AddTaskAsync(RichContent.FromText("数据库中的最新任务"), null);
        var export = Path.Combine(_root, "arbitrary-name.db"); await Library.ExportAsync(book, export);
        var other = new BookLibrary(Path.Combine(_root, "Other")); using var prepared = await other.PrepareImportAsync(export);
        Assert.Equal("Work", prepared.Info.Name); var imported = await other.ImportAsync(prepared, null, ImportMode.Merge);
        Assert.Equal("Work.db", Path.GetFileName(imported.Path));
        Assert.Single((await new TaskRepository(imported.Path).QueryAsync(new(null, TaskFilter.Open, TaskSort.Created))).Items);
    }
    [Fact] public async Task MergeUsesLatestWholeVersionAndIsIdempotent()
    {
        var (book, repo) = await Create(); var original = await repo.AddTaskAsync(RichContent.FromText("原始内容"), null, "共同项目");
        var external = Path.Combine(_root, "external.db"); await Library.ExportAsync(book, external);
        var importedRepo = new TaskRepository(external);
        var changed = await importedRepo.UpdateContentAsync(original.Id, RichContent.FromText("导入端的更新"), original.Revision);
        await importedRepo.SetStatusAsync(changed.Id, TodoStatus.Completed, changed.Revision);
        var localOnly = await repo.AddTaskAsync(RichContent.FromText("本地独有"), null, "同名新项目");
        await importedRepo.AddTaskAsync(RichContent.FromText("导入独有"), null, "同名新项目");
        for (int i = 0; i < 2; i++) { using var prepared = await Library.PrepareImportAsync(external); await Library.ImportAsync(prepared, book, ImportMode.Merge); }
        var done = Assert.Single((await repo.QueryAsync(new(null, TaskFilter.Completed, TaskSort.Completed))).Items);
        Assert.Equal("导入端的更新", done.PlainText); Assert.Equal(2, (await repo.GetProjectsAsync()).Count);
        var open = await repo.QueryAsync(new(null, TaskFilter.Open, TaskSort.Created)); Assert.Equal(2, open.Items.Count);
        Assert.All(open.Items, t => Assert.Equal(localOnly.ProjectId, t.ProjectId));
        Assert.Equal(2, Directory.GetFiles(Library.BackupDirectory, "*.db").Length);
        using var c = Database.Open(book.Path); Assert.Equal(1L, Database.Scalar(c, "SELECT COUNT(*) FROM TaskStateEvents"));
    }
    [Fact] public async Task MergeTieKeepsLocalAndDifferentIdsWithSameTextArePreserved()
    {
        var (book, repo) = await Create(); var task = await repo.AddTaskAsync(RichContent.FromText("相同内容"), null);
        var external = Path.Combine(_root, "external.db"); await Library.ExportAsync(book, external);
        using (var c = Database.Open(external)) Database.Exec(c, "UPDATE Tasks SET PlainText=$text,ContentJson=$json WHERE Id=$id", ("$text", "冲突内容"), ("$json", RichContent.FromText("冲突内容").ToJson()), ("$id", task.Id));
        await new TaskRepository(external).AddTaskAsync(RichContent.FromText("相同内容"), null);
        using var p = await Library.PrepareImportAsync(external); await Library.ImportAsync(p, book, ImportMode.Merge);
        var rows = (await repo.QueryAsync(new(null, TaskFilter.Open, TaskSort.Created))).Items;
        Assert.Equal(2, rows.Count); Assert.All(rows, r => Assert.Equal("相同内容", r.PlainText));
    }
    [Fact] public async Task ReplaceMakesRecoverableBackupAndPreservesGlobalSettings()
    {
        var (book, repo) = await Create(); var external = Path.Combine(_root, "empty.db"); await Library.ExportAsync(book, external);
        await repo.AddTaskAsync(RichContent.FromText("需要备份"), null); Library.SaveSettings(new() { Palette = "蜜桃" });
        using var p = await Library.PrepareImportAsync(external); await Library.ImportAsync(p, book, ImportMode.Replace);
        Assert.Empty((await repo.QueryAsync(new(null, TaskFilter.Open, TaskSort.Created))).Items);
        var backup = Assert.Single(Directory.GetFiles(Library.BackupDirectory, "*.db"));
        Assert.Single((await new TaskRepository(backup).QueryAsync(new(null, TaskFilter.Open, TaskSort.Created))).Items);
        Assert.Equal("蜜桃", Library.LoadSettings().Palette); Assert.Single(await Library.ListAsync());
    }
    [Fact] public async Task InvalidOrFutureImportLeavesExistingBookUntouched()
    {
        var (book, repo) = await Create(); await repo.AddTaskAsync(RichContent.FromText("保留"), null);
        var invalid = Path.Combine(_root, "invalid.db"); await File.WriteAllTextAsync(invalid, "not a database");
        await Assert.ThrowsAnyAsync<Exception>(() => Library.PrepareImportAsync(invalid));
        var future = Path.Combine(_root, "future.db"); await Library.ExportAsync(book, future);
        using (var c = Database.Open(future)) Database.Exec(c, "PRAGMA user_version=999");
        await Assert.ThrowsAsync<InvalidDataException>(() => Library.PrepareImportAsync(future));
        Assert.Single((await repo.QueryAsync(new(null, TaskFilter.Open, TaskSort.Created))).Items);
    }
    [Fact] public async Task CancellationDoesNotReplaceOrLoseLocalBook()
    {
        var (book, repo) = await Create(); await repo.AddTaskAsync(RichContent.FromText("保留"), null);
        var export = Path.Combine(_root, "export.db"); await Library.ExportAsync(book, export);
        using var p = await Library.PrepareImportAsync(export); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Library.ImportAsync(p, book, ImportMode.Replace, cts.Token));
        Assert.Single((await repo.QueryAsync(new(null, TaskFilter.Open, TaskSort.Created))).Items);
    }
    [Fact] public async Task ExistingTaskCanMoveModulesAtomically()
    {
        var (_, repo) = await Create();
        var first = await repo.AddProjectAsync("模块一"); var second = await repo.AddProjectAsync("模块二");
        var task = await repo.AddTaskAsync(RichContent.FromText("原内容"), first.Id);
        var moved = await repo.UpdateTaskAsync(task.Id, RichContent.FromText("新内容"), second.Id, task.Revision);
        Assert.Equal(second.Id, moved.ProjectId); Assert.Equal("新内容", moved.PlainText);
        var unassigned = await repo.UpdateTaskAsync(task.Id, RichContent.FromText("新内容"), null, moved.Revision);
        Assert.Null(unassigned.ProjectId);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.UpdateTaskAsync(task.Id, RichContent.FromText("无效"), "missing", unassigned.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.UpdateTaskAsync(task.Id, RichContent.FromText("冲突"), first.Id, task.Revision));
        var deleted = await repo.DeleteTaskAsync(task.Id, unassigned.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.UpdateTaskAsync(task.Id, RichContent.FromText("删除后"), first.Id, deleted.Revision));
    }
    private static void Seed(string path, int count)
    {
        using var c = Database.Open(path); using var tx = c.BeginTransaction();
        var json = RichContent.FromText("同一时间的任务").ToJson();
        using var cmd = Database.Command(c, "INSERT INTO Tasks(Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision) VALUES($id,NULL,$json,'同一时间的任务',0,1700000000000,1700000000000,NULL,1)", ("$id", ""), ("$json", json));
        for (int i = 0; i < count; i++) { cmd.Parameters["$id"].Value = i.ToString("x32"); cmd.ExecuteNonQuery(); }
        tx.Commit(); Database.Exec(c, "ANALYZE");
    }
}
