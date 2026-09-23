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

public sealed record ReportSnapshot(string BookTitle, IReadOnlyList<ProjectInfo> Projects, IReadOnlyList<TodoItem> Tasks, IReadOnlyDictionary<string, string>? Colors = null);
public enum ReportLineKind { Title, Heading, DateHeading, TaskContent, Metadata, Text, Body }
public enum ReportColor { Default, Open, Verification, Completed, Deleted }
public sealed record ReportRun(string Text, ReportColor Color = ReportColor.Default);
public sealed record ReportLine(ReportLineKind Kind, IReadOnlyList<ReportRun> Runs)
{
    public ReportLine(ReportLineKind kind, string text) : this(kind, [new(text)]) { }
    public string Text => string.Concat(Runs.Select(r => r.Text));
}
public sealed record TaskReport(IReadOnlyList<ReportLine> Lines)
{
    public IReadOnlyDictionary<ReportColor, string> Colors { get; init; } = DefaultColors;
    public static IReadOnlyDictionary<ReportColor, string> DefaultColors { get; } = new Dictionary<ReportColor, string>
    {
        [ReportColor.Open] = "#42E9FF", [ReportColor.Verification] = "#FFE500", [ReportColor.Completed] = "#00FF73", [ReportColor.Deleted] = "#FF5141", [ReportColor.Default] = "#000000"
    };
    public string ResolveColor(ReportColor color) => Colors.GetValueOrDefault(color, DefaultColors[color]);
    public static string Color(ReportColor color) => color switch
    {
        ReportColor.Open => "#42E9FF",
        ReportColor.Verification => "#FFE500",
        ReportColor.Completed => "#00FF73",
        ReportColor.Deleted => "#FF5141",
        _ => "#000000"
    };
    public string ToMarkdown()
    {
        var output = new StringBuilder();
        foreach (var line in Lines)
        {
            // Runs contain only model-generated styling. Every text value, including task
            // content, is escaped before it can enter Markdown or an HTML span.
            var text = string.Concat(line.Runs.Select(run => run.Color == ReportColor.Default
                ? Escape(run.Text)
                : $"<span style=\"color: {ResolveColor(run.Color)};\">{Escape(run.Text)}</span>"));
            output.AppendLine(line.Kind switch
            {
                ReportLineKind.Title => "# " + text,
                ReportLineKind.Heading => "## " + text,
                ReportLineKind.DateHeading => "### " + text,
                _ => text + "  "
            });
            if (line.Kind is ReportLineKind.Title or ReportLineKind.Heading or ReportLineKind.DateHeading) output.AppendLine();
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
        static (string Label, ReportColor Color) Status(TodoStatus status) => status switch
        {
            TodoStatus.Open => ("未完成", ReportColor.Open),
            TodoStatus.Verification => ("待验证", ReportColor.Verification),
            TodoStatus.Completed => ("已完成", ReportColor.Completed),
            _ => ("已删除", ReportColor.Deleted)
        };
        int Count(TodoStatus status) => tasks.Count(t => t.Status == status);
        var lines = new List<ReportLine>
        {
            new(ReportLineKind.Title, snapshot.BookTitle + " · 任务报告"),
            new(ReportLineKind.Text, "模块：" + (options.ProjectId == null ? "全部模块" : Project(options.ProjectId))),
            new(ReportLineKind.Text, "状态：" + options.StatusLabel),
            new(ReportLineKind.Text, "添加时间：" + options.Dates.Label),
            new(ReportLineKind.Text, "导出时间：" + exportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")),
            new(ReportLineKind.Heading, "概要"),
            new(ReportLineKind.Text, new ReportRun[]
            {
                new($"共 {tasks.Count} 条任务；未完成 "), new(Count(TodoStatus.Open).ToString(), ReportColor.Open),
                new("；待验证 "), new(Count(TodoStatus.Verification).ToString(), ReportColor.Verification),
                new("；已完成 "), new(Count(TodoStatus.Completed).ToString(), ReportColor.Completed),
                new("；已删除 "), new(Count(TodoStatus.Deleted).ToString(), ReportColor.Deleted), new("。")
            })
        };
        if (tasks.Count == 0) lines.Add(new(ReportLineKind.Text, "所选条件下没有任务。"));
        DateTime? group = null;
        for (int i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            var createdDay = DateTimeOffset.FromUnixTimeMilliseconds(task.CreatedAt).LocalDateTime.Date;
            if (createdDay != group)
            {
                group = createdDay;
                lines.Add(new(ReportLineKind.DateHeading, $"[{createdDay:yyyy-MM-dd}]"));
            }
            var body = task.PlainText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            lines.Add(new(ReportLineKind.TaskContent, $"{i + 1}. {body[0]}"));
            foreach (var continuation in body.Skip(1)) lines.Add(new(ReportLineKind.Body, "   " + continuation));
            var status = Status(task.Status);
            var metadata = new List<ReportRun>
            {
                new($"[{status.Label}]", status.Color), new("  " + Project(task.ProjectId) + "  添加时间：" + Stamp(task.CreatedAt))
            };
            if ((task.CompletedAt ?? task.PreviousCompletedAt) is { } completed) metadata.Add(new("  完成时间：" + Stamp(completed)));
            if (task.DeletedAt is { } deleted) metadata.Add(new("  删除时间：" + Stamp(deleted)));
            lines.Add(new(ReportLineKind.Metadata, metadata));
            lines.Add(new(ReportLineKind.Text, ""));
        }
        var colors = new Dictionary<ReportColor, string>(DefaultColors);
        if (snapshot.Colors != null)
        {
            if (snapshot.Colors.TryGetValue("StatusColor.Open", out var open)) colors[ReportColor.Open] = open;
            if (snapshot.Colors.TryGetValue("StatusColor.Verification", out var verification)) colors[ReportColor.Verification] = verification;
            if (snapshot.Colors.TryGetValue("StatusColor.Completed", out var completed)) colors[ReportColor.Completed] = completed;
        }
        return new(lines) { Colors = colors };
    }
}
