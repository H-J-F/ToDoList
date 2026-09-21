using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.Tests;

public sealed class ReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ToDoList-reports-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Fact] public void DateSelectionsIncludeLastDayAndHandleLeapYearsAndDst()
    {
        var year = new DateSelection(DateScope.Year, new(2024, 7, 12), default).Bounds(TimeZoneInfo.Utc);
        Assert.Equal(366L * 86400000, year.Until - year.From);
        var month = new DateSelection(DateScope.Month, new(2024, 2, 12), default).Bounds(TimeZoneInfo.Utc);
        Assert.Equal(29L * 86400000, month.Until - month.From);
        var day = new DateSelection(DateScope.Day, new(2026, 3, 8), default).Bounds(TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
        Assert.Equal(23L * 3600000, day.Until - day.From);
        var range = new DateSelection(DateScope.Range, new(2025, 12, 31), new(2026, 1, 1)).Bounds(TimeZoneInfo.Utc);
        Assert.Equal(2L * 86400000, range.Until - range.From);
        Assert.Throws<ArgumentException>(() => new DateSelection(DateScope.Range, new(2026, 2, 2), new(2026, 2, 1)).Bounds());
        Assert.Throws<ArgumentException>(() => new DateSelection(DateScope.Year, new(9999, 1, 1), default).Bounds());
        Assert.Equal((null, (long?)null), DateSelection.Any.Bounds());
    }
    [Fact] public async Task ReportReadsBeyondUiLimitsAndCombinesModuleStatusAndDate()
    {
        var book = await new BookLibrary(_root).CreateAsync("Report", "报告测试"); var repo = new TaskRepository(book.Path);
        var project = await repo.AddProjectAsync("工作"); var start = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        using (var c = Database.Open(book.Path))
        using (var tx = c.BeginTransaction())
        {
            for (int i = 0; i < 2405; i++)
            {
                var status = (TodoStatus)(i % 4); var stamp = start + i;
                TaskRepository.Insert(c, new(i.ToString("D6"), i % 2 == 0 ? project.Id : null, RichContent.FromText("任务" + i).ToJson(), "任务" + i,
                    status, stamp, stamp, status == TodoStatus.Completed ? stamp : null, 1,
                    status == TodoStatus.Deleted ? stamp : null, status == TodoStatus.Deleted ? TodoStatus.Open : null));
            }
            tx.Commit();
        }
        var all = new ReportOptions(null, TaskFilter.All, DateSelection.Any, ReportFormat.Markdown);
        var snapshot = await repo.ReadReportAsync(all); Assert.Equal(2405, snapshot.Tasks.Count);
        Assert.Equal("000000", snapshot.Tasks[0].Id); Assert.Equal("002404", snapshot.Tasks[^1].Id);
        foreach (var projectId in new[] { (string?)null, project.Id })
        foreach (var status in new[] { TaskFilter.All, TaskFilter.Open, TaskFilter.Completed, TaskFilter.Deleted })
        {
            var option = all with { ProjectId = projectId, Status = status, Dates = new(DateScope.Day, DateTime.Today, DateTime.Today) };
            var filtered = await repo.ReadReportAsync(option);
            Assert.Equal(snapshot.Tasks.Where(option.Query().Matches).Select(t => t.Id), filtered.Tasks.Select(t => t.Id));
        }
        Assert.Equal(200, (await repo.QueryAsync(all.Query())).Items.Count);
        var future = await repo.ReadReportAsync(all with { Dates = new(DateScope.Day, DateTime.Today.AddDays(2), default) }); Assert.Empty(future.Tasks);
        var bounds = new DateSelection(DateScope.Day, DateTime.Today, default).Bounds();
        var query = new TaskQuery(null, TaskFilter.Calendar, TaskSort.Created, bounds.From, bounds.Until);
        Assert.True(query.Matches(snapshot.Tasks[0] with { CreatedAt = bounds.From!.Value }));
        Assert.False(query.Matches(snapshot.Tasks[0] with { CreatedAt = bounds.Until!.Value }));
    }
    [Fact] public void ReportIncludesMetadataLiteralTextAndDeletedCompletionTime()
    {
        var content = RichContent.FromText("中文 👩‍💻 <xml>& **正文**\n第二行\n\n末行");
        var task = new TodoItem("a", "p", content.ToJson(), content.PlainText, TodoStatus.Deleted, 1, 3, null, 1, 3, TodoStatus.Completed, 2);
        var report = TaskReport.Create(new("测试书", [new("p", "工作")], [task]), new("p", TaskFilter.All, DateSelection.Any, ReportFormat.Markdown), DateTimeOffset.Now);
        Assert.Contains(report.Lines, l => l.Text.StartsWith("完成时间："));
        Assert.Contains(report.Lines, l => l.Text.Contains("已删除 1"));
        Assert.Equal(content.PlainText.Split('\n'), report.Lines.Where(l => l.Kind == ReportLineKind.Body).Select(l => l.Text));
        Assert.Contains("\\*\\*正文\\*\\*", report.ToMarkdown());
        var empty = TaskReport.Create(new("空书", [], []), new(null, TaskFilter.All, DateSelection.Any, ReportFormat.Markdown), DateTimeOffset.Now);
        Assert.Contains(empty.Lines, l => l.Text == "所选条件下没有任务。");
    }
}
