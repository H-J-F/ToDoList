using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToDoList.Core;

public enum TodoStatus { Open, Verification, Completed, Deleted }
public enum TaskFilter { Completed, Open, Month, Week, Today, Deleted }
public enum TaskSort { Created, Completed, Deleted }
public enum PageDirection { Older, Newer }
public enum ImportMode { Merge, Replace }

public sealed record BookInfo(string Name, string Title, string Path);
public sealed record ProjectInfo(string? Id, string Name);
public sealed record TodoItem(string Id, string? ProjectId, string ContentJson, string PlainText,
    TodoStatus Status, long CreatedAt, long UpdatedAt, long? CompletedAt, long Revision, long? DeletedAt = null, TodoStatus? PreviousStatus = null, long? PreviousCompletedAt = null);
public sealed record PageCursor(long Time, string Id);
public sealed record TaskQuery(string? ProjectId, TaskFilter Filter, TaskSort Sort,
    long? From = null, long? Until = null, PageCursor? Cursor = null,
    PageDirection Direction = PageDirection.Older, int PageSize = 200, bool IncludeCursor = false)
{
    public bool ByCompletion => Filter == TaskFilter.Completed && Sort == TaskSort.Completed;
    public bool ByDeletion => Filter == TaskFilter.Deleted;
    public long SortTime(TodoItem item) => ByDeletion ? item.DeletedAt!.Value : ByCompletion ? item.CompletedAt!.Value : item.CreatedAt;
    public PageCursor CursorFor(TodoItem item) => new(SortTime(item), item.Id);
    public bool Matches(TodoItem item) => (ProjectId == null || ProjectId == item.ProjectId)
        && (Filter != TaskFilter.Open || item.Status is TodoStatus.Open or TodoStatus.Verification)
        && (Filter != TaskFilter.Deleted || item.Status == TodoStatus.Deleted)
        && (Filter != TaskFilter.Completed || item.Status == TodoStatus.Completed)
        && (!From.HasValue || item.CreatedAt >= From) && (!Until.HasValue || item.CreatedAt < Until);
}
public sealed record TaskPage(IReadOnlyList<TodoItem> Items, bool HasMore);
public sealed record RichRun(string Text, bool Bold = false, bool Italic = false,
    bool Underline = false, string? Color = null, string? Link = null);
public sealed record RichParagraph(List<RichRun> Runs);
public sealed record RichContent(int Version, List<RichParagraph> Paragraphs)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string PlainText => string.Join("\n", Paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.Text))));
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static RichContent FromText(string text) => new(1, text.Replace("\r\n", "\n").Split('\n')
        .Select(line => new RichParagraph([new RichRun(line)])).ToList());
    public static RichContent Parse(string json)
    {
        var content = JsonSerializer.Deserialize<RichContent>(json, JsonOptions)
            ?? throw new InvalidDataException("任务内容无法读取。");
        if (content.Version != 1 || content.Paragraphs == null || content.Paragraphs.Count == 0)
            throw new InvalidDataException("不支持的富文本格式。");
        foreach (var p in content.Paragraphs)
        {
            if (p?.Runs == null) throw new InvalidDataException("富文本段落不完整。");
            foreach (var r in p.Runs)
            {
                if (r?.Text == null) throw new InvalidDataException("富文本内容不完整。");
                if (r.Color != null && !Regex.IsMatch(r.Color, "^#[0-9a-fA-F]{6}$"))
                    throw new InvalidDataException("不支持的文字颜色。");
                if (r.Link != null && !IsSafeLink(r.Link)) throw new InvalidDataException("链接仅支持 http / https。");
            }
        }
        return content;
    }
    public static bool IsSafeLink(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";
}

public static partial class BookRules
{
    [GeneratedRegex(@"\A[A-Za-z0-9]{1,64}\z")]
    private static partial Regex NamePattern();
    public static void ValidateName(string name)
    {
        if (!NamePattern().IsMatch(name) || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("标识名只能包含 1–64 个英文字母或数字，且不能使用 Windows 保留名称。");
    }
    public static string ValidateTitle(string title)
    {
        title = title.Trim();
        if (title.Length is < 1 or > 100) throw new ArgumentException("名称需为 1–100 个字符。");
        return title;
    }
}

public static class DateRanges
{
    public static (long? From, long? Until) For(TaskFilter filter, DateTime localNow, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var day = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified);
        DateTime start, end;
        switch (filter)
        {
            case TaskFilter.Today: start = day; end = start.AddDays(1); break;
            case TaskFilter.Week: start = day.AddDays(-((7 + (int)day.DayOfWeek - 1) % 7)); end = start.AddDays(7); break;
            case TaskFilter.Month: start = new(day.Year, day.Month, 1); end = start.AddMonths(1); break;
            default: return (null, null);
        }
        static long Convert(DateTime date, TimeZoneInfo tz)
        {
            while (tz.IsInvalidTime(date)) date = date.AddMinutes(1);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date, tz)).ToUnixTimeMilliseconds();
        }
        return (Convert(start, zone), Convert(end, zone));
    }
}

public sealed class AppSettings
{
    public List<string> RecentColors { get; set; } = [];
    public List<string> RecentEmoji { get; set; } = [];
    public int SettingsVersion { get; set; }
    public string? AccentPreset { get; set; }
    public string Mode { get; set; } = "浅色";
    public string Palette { get; set; } = "奶油";
    public double FontSize { get; set; } = 14;
    public string Density { get; set; } = "舒适";
    public bool ReduceMotion { get; set; }
    public string? LastBook { get; set; }
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 760;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }
}

public interface ITaskRepository
{
    Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken ct = default);
    Task<ProjectInfo> AddProjectAsync(string name, CancellationToken ct = default);
    Task<TaskPage> QueryAsync(TaskQuery query, CancellationToken ct = default);
    Task<TodoItem> AddTaskAsync(RichContent content, string? projectId, string? newProject = null, CancellationToken ct = default);
    Task<TodoItem> UpdateContentAsync(string id, RichContent content, long revision, CancellationToken ct = default);
    Task<TodoItem> DeleteTaskAsync(string id, long revision, CancellationToken ct = default);
    Task<TodoItem> RestoreTaskAsync(string id, long revision, CancellationToken ct = default);
    Task<TodoItem> SetStatusAsync(string id, TodoStatus status, long revision, CancellationToken ct = default);
}
