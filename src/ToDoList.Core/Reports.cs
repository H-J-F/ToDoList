using System.Text;

namespace ToDoList.Core;

public enum DateScope { Any, Year, Month, Day, Range }
public enum ReportFormat { Markdown, Docx, Pdf }

public sealed record DateSelection(DateScope Scope, DateTime Start, DateTime End)
{
    public static DateSelection Any => new(DateScope.Any, DateTime.Today, DateTime.Today);
    public (DateTime Start, DateTime End)? Days => Scope switch
    {
        DateScope.Any => null,
        DateScope.Year => (new(Start.Year, 1, 1), new(Start.Year, 12, 31)),
        DateScope.Month => (new(Start.Year, Start.Month, 1), new(Start.Year, Start.Month, DateTime.DaysInMonth(Start.Year, Start.Month))),
        DateScope.Day => (Start.Date, Start.Date),
        DateScope.Range when End.Date >= Start.Date => (Start.Date, End.Date),
        _ => throw new ArgumentException("结束日期不能早于开始日期。")
    };
    public (long? From, long? Until) Bounds(TimeZoneInfo? zone = null)
    {
        if (Days is not { } days) return (null, null);
        if (days.End == DateTime.MaxValue.Date) throw new ArgumentException("结束日期必须早于 9999 年 12 月 31 日。");
        zone ??= TimeZoneInfo.Local;
        static long Stamp(DateTime day, TimeZoneInfo tz)
        {
            day = DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
            while (tz.IsInvalidTime(day)) day = day.AddMinutes(1);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day, tz)).ToUnixTimeMilliseconds();
        }
        return (Stamp(days.Start, zone), Stamp(days.End.AddDays(1), zone));
    }
    public string Label => Scope switch
    {
        DateScope.Any => "不限时间",
        DateScope.Year => Start.ToString("yyyy年"),
        DateScope.Month => Start.ToString("yyyy年M月"),
        DateScope.Day => Start.ToString("yyyy年M月d日"),
        _ => $"{Start:yyyy年M月d日} 至 {End:yyyy年M月d日}"
    };
}

public sealed record ReportOptions(string? ProjectId, TaskFilter Status, DateSelection Dates, ReportFormat Format)
{
    public TaskQuery Query()
    {
        if (Status is not (TaskFilter.All or TaskFilter.Open or TaskFilter.Completed or TaskFilter.Deleted))
            throw new ArgumentException("不支持的报告任务状态。");
        var range = Dates.Bounds();
        return new(ProjectId, Status, TaskSort.Created, range.From, range.Until);
    }
    public string StatusLabel => Status switch { TaskFilter.Open => "未完成（含待验证）", TaskFilter.Completed => "已完成", TaskFilter.Deleted => "已删除", _ => "所有" };
}

public sealed record ReportSnapshot(string BookTitle, IReadOnlyList<ProjectInfo> Projects, IReadOnlyList<TodoItem> Tasks);
public enum ReportLineKind { Title, Heading, Text, Body }
public sealed record ReportLine(ReportLineKind Kind, string Text);
public sealed record TaskReport(IReadOnlyList<ReportLine> Lines)
{
    public string ToMarkdown()
    {
        var output = new StringBuilder();
        foreach (var line in Lines)
        {
            // Escape task text so literal Markdown cannot change the report's structure.
            var text = Escape(line.Text);
            output.AppendLine(line.Kind switch { ReportLineKind.Title => "# " + text, ReportLineKind.Heading => "## " + text, _ => text + "  " });
            if (line.Kind is ReportLineKind.Title or ReportLineKind.Heading) output.AppendLine();
        }
        return output.ToString();
    }
    private static string Escape(string text)
    {
        var result = new StringBuilder();
        foreach (var ch in text) { if ("\\`*_{}[]<>()#+-.!|&~".Contains(ch)) result.Append('\\'); result.Append(ch); }
        return result.ToString();
    }
    public static TaskReport Create(ReportSnapshot snapshot, ReportOptions options, DateTimeOffset exportedAt)
    {
        var projects = snapshot.Projects.ToDictionary(p => p.Id!, p => p.Name);
        string Project(string? id) => id == null ? "未分配模块" : projects.GetValueOrDefault(id, "未知模块");
        string Stamp(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        var tasks = snapshot.Tasks.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
        var lines = new List<ReportLine>
        {
            new(ReportLineKind.Title, snapshot.BookTitle + " · 任务报告"),
            new(ReportLineKind.Text, "模块：" + (options.ProjectId == null ? "全部模块" : Project(options.ProjectId))),
            new(ReportLineKind.Text, "状态：" + options.StatusLabel),
            new(ReportLineKind.Text, "添加时间：" + options.Dates.Label),
            new(ReportLineKind.Text, "导出时间：" + exportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")),
            new(ReportLineKind.Heading, "概要"),
            new(ReportLineKind.Text, $"共 {tasks.Count} 条任务；未完成 {tasks.Count(t => t.Status == TodoStatus.Open)}；待验证 {tasks.Count(t => t.Status == TodoStatus.Verification)}；已完成 {tasks.Count(t => t.Status == TodoStatus.Completed)}；已删除 {tasks.Count(t => t.Status == TodoStatus.Deleted)}。")
        };
        if (tasks.Count == 0) lines.Add(new(ReportLineKind.Text, "所选条件下没有任务。"));
        for (int i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            var status = task.Status switch { TodoStatus.Open => "未完成", TodoStatus.Verification => "待验证", TodoStatus.Completed => "已完成", _ => "已删除" };
            lines.Add(new(ReportLineKind.Heading, $"{i + 1}. [{status}] {Project(task.ProjectId)}"));
            lines.Add(new(ReportLineKind.Text, "添加时间：" + Stamp(task.CreatedAt)));
            if ((task.CompletedAt ?? task.PreviousCompletedAt) is { } completed) lines.Add(new(ReportLineKind.Text, "完成时间：" + Stamp(completed)));
            if (task.DeletedAt is { } deleted) lines.Add(new(ReportLineKind.Text, "删除时间：" + Stamp(deleted)));
            foreach (var line in task.PlainText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) lines.Add(new(ReportLineKind.Body, line));
        }
        return new(lines);
    }
}
