using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.Tests;

public sealed class EditTimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "todo-edit-time-" + Guid.NewGuid().ToString("N"));
    private BookLibrary Library => new(Path.Combine(_root, "Data"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task OnlyActualContentOrProjectChangesCountAsEdits()
    {
        var book = await Library.CreateAsync("Edits", "修改时间"); var repo = new TaskRepository(book.Path);
        var content = RichContent.FromText("任务");
        var task = await repo.AddTaskAsync(content, null);
        Assert.Null(task.EditedAt);
        Assert.Equal(task, await repo.UpdateContentAsync(task.Id, content, task.Revision));
        Assert.Equal(task, await repo.UpdateTaskAsync(task.Id, content, null, task.Revision));
        var formatted = new RichContent(1, [new([new("任务", Bold: true)])]);
        var edited = await repo.UpdateContentAsync(task.Id, formatted, task.Revision);
        Assert.Equal(edited.UpdatedAt, edited.EditedAt); Assert.True(edited.EditedAt > task.UpdatedAt);
        Assert.Equal(edited, await repo.UpdateTaskAsync(edited.Id, formatted, null, edited.Revision));
        var project = await repo.AddProjectAsync("模块");
        var moved = await repo.UpdateTaskAsync(edited.Id, formatted, project.Id, edited.Revision);
        Assert.True(moved.EditedAt > edited.EditedAt);
        var current = moved;
        foreach (var status in new[] { TodoStatus.Verification, TodoStatus.Completed, TodoStatus.Open })
        {
            current = await repo.SetStatusAsync(current.Id, status, current.Revision);
            Assert.Equal(moved.EditedAt, current.EditedAt);
        }
        current = await repo.DeleteTaskAsync(current.Id, current.Revision);
        Assert.Equal(moved.EditedAt, current.EditedAt);
        current = await repo.RestoreTaskAsync(current.Id, current.Revision);
        Assert.Equal(moved.EditedAt, current.EditedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.UpdateContentAsync(current.Id, content, task.Revision));
        Assert.Equal(current, Assert.Single((await repo.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items));
    }

    [Fact]
    public async Task RemovingProjectEditsAffectedTasksIncludingDeletedTasks()
    {
        var book = await Library.CreateAsync("Projects", "模块"); var repo = new TaskRepository(book.Path);
        var project = await repo.AddProjectAsync("模块");
        var task = await repo.AddTaskAsync(RichContent.FromText("关联"), project.Id);
        task = await repo.DeleteTaskAsync(task.Id, task.Revision);
        var other = await repo.AddTaskAsync(RichContent.FromText("无关联"), null);
        await repo.DeleteProjectAsync(project.Id!);
        var all = (await repo.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items;
        var changed = all.Single(t => t.Id == task.Id);
        Assert.Null(changed.ProjectId); Assert.True(changed.EditedAt > task.UpdatedAt);
        Assert.Equal(changed.EditedAt, changed.UpdatedAt); Assert.Equal(task.Revision + 1, changed.Revision);
        Assert.Equal(task.DeletedAt, changed.DeletedAt); Assert.Equal(other, all.Single(t => t.Id == other.Id));
        using var c = Database.Open(book.Path); BookLibrary.Validate(c, book.Path);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LegacyUpgradePreservesTasksAndImportSource(int version)
    {
        var book = await Library.CreateAsync("Legacy", "旧数据"); var repo = new TaskRepository(book.Path);
        var task = await repo.AddTaskAsync(RichContent.FromText("已有任务"), null);
        using (var c = Database.Open(book.Path))
        {
            Database.Exec(c, "ALTER TABLE Tasks DROP COLUMN EditedAt;");
            if (version == 2) Database.Exec(c, "ALTER TABLE Projects DROP COLUMN SortOrder;");
            Database.Exec(c, $"PRAGMA user_version={version};");
        }
        using (var prepared = await Library.PrepareImportAsync(book.Path))
        {
            using var source = Database.Open(book.Path);
            Assert.Equal((long)version, Database.Scalar(source, "PRAGMA user_version"));
            var copy = new TaskRepository(prepared.TemporaryPath);
            Assert.Equal(task, Assert.Single((await copy.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items));
        }
        Database.Upgrade(book.Path, Library.BackupDirectory);
        Database.Upgrade(book.Path, Library.BackupDirectory);
        Assert.Equal(task, Assert.Single((await repo.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items));
        using var backup = Database.Open(Assert.Single(Directory.GetFiles(Library.BackupDirectory, "*.db")));
        Assert.Equal((long)version, Database.Scalar(backup, "PRAGMA user_version"));
        using var result = Database.Open(book.Path); BookLibrary.Validate(result, book.Path);
        Assert.Equal(4L, Database.Scalar(result, "PRAGMA user_version"));
    }

    [Fact]
    public async Task ExportMergeAndReportsPreserveIndependentEditTime()
    {
        var book = await Library.CreateAsync("Reports", "报告"); var repo = new TaskRepository(book.Path);
        var task = await repo.AddTaskAsync(RichContent.FromText("初始内容"), null);
        task = await repo.UpdateContentAsync(task.Id, RichContent.FromText("修改内容"), task.Revision);
        var file = Path.Combine(_root, "export.db"); await Library.ExportAsync(book, file);
        var remote = new TaskRepository(file);
        var edited = await remote.UpdateTaskAsync(task.Id, RichContent.FromText("再次修改"), null, task.Revision);
        var completed = await remote.SetStatusAsync(task.Id, TodoStatus.Completed, edited.Revision);
        using (var prepared = await Library.PrepareImportAsync(file)) await Library.ImportAsync(prepared, book, ImportMode.Merge);
        var merged = Assert.Single((await repo.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items);
        Assert.Equal(edited.EditedAt, merged.EditedAt); Assert.Equal(completed.UpdatedAt, merged.UpdatedAt);
        var options = new ReportOptions(null, TaskFilter.All, DateSelection.Any, ReportFormat.Markdown);
        var report = TaskReport.Create(await repo.ReadReportAsync(options), options, DateTimeOffset.Now);
        Assert.Contains("上次修改时间：", report.ToMarkdown());
        Assert.Contains(report.Lines, line => line.Kind == ReportLineKind.Metadata && line.Text.Contains(DateTimeOffset.FromUnixTimeMilliseconds(edited.EditedAt!.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        var fresh = await repo.AddTaskAsync(RichContent.FromText("新任务"), null);
        var plainReport = TaskReport.Create(new("新书", [], [fresh]), options, DateTimeOffset.Now);
        Assert.DoesNotContain("上次修改时间", plainReport.ToMarkdown());
        Assert.Equal("#00CAE5", report.ResolveColor(ReportColor.Open));
        Assert.Equal("#E6A409", report.ResolveColor(ReportColor.Verification));
        Assert.Equal("#07E355", report.ResolveColor(ReportColor.Completed));
        await repo.SetSettingAsync("StatusColor.Completed", "#123456");
        var custom = TaskReport.Create(await repo.ReadReportAsync(options), options, DateTimeOffset.Now);
        Assert.Equal("#123456", custom.ResolveColor(ReportColor.Completed));
    }

    [Fact]
    public async Task ImportRejectsImpossibleEditTime()
    {
        var book = await Library.CreateAsync("Invalid", "无效时间"); var repo = new TaskRepository(book.Path);
        await repo.AddTaskAsync(RichContent.FromText("任务"), null);
        using (var c = Database.Open(book.Path)) Database.Exec(c, "UPDATE Tasks SET EditedAt=UpdatedAt+1;");
        await Assert.ThrowsAsync<InvalidDataException>(() => Library.PrepareImportAsync(book.Path));
    }
}
