using System.Collections.ObjectModel;
using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public BookLibrary Library { get; }
    public AppSettings Settings { get; }
    public ObservableCollection<BookInfo> Books { get; } = [];
    public ObservableCollection<ProjectInfo> Projects { get; } = [];
    public ObservableCollection<ProjectInfo> DraftProjects { get; } = [];
    public ObservableCollection<TaskViewModel> Tasks { get; } = [];
    public TaskRepository? Repository { get; private set; }
    private CancellationTokenSource _queryCancellation = new();
    private int _epoch;
    private bool _paging;
    private TaskCompletionSource? _pageCompletion;
    public bool HasOlder { get; private set; }
    public bool HasNewer { get; private set; }
    public int QueryEpoch => _epoch;
    public TaskQuery? CurrentQuery { get; private set; }
    public event Action? LayoutChanging;
    public event Action? LayoutChanged;
    public event Action? LatestRequested;
    public Func<TaskViewModel, Task>? AnimateRemoval { get; set; }
    public Func<bool>? IsEditing { get; set; }
    private BookInfo? _book;
    public BookInfo? CurrentBook { get => _book; private set { Set(ref _book, value); Raise(nameof(HasBook)); Raise(nameof(BookTitle)); } }
    public bool HasBook => CurrentBook != null;
    public string BookTitle => CurrentBook?.Title ?? "我的第一本待办书";
    public string? CurrentProjectId { get; private set; }
    public TaskFilter Filter { get; private set; } = TaskFilter.Today;
    public DateSelection SelectedDates { get; private set; } = new(DateScope.Day, DateTime.Today, DateTime.Today);
    public bool IsCalendarFilter => Filter == TaskFilter.Calendar;
    public TaskSort Sort { get; private set; } = TaskSort.Completed;
    public string FilterTitle => Filter switch { TaskFilter.All => "所有", TaskFilter.Calendar => SelectedDates.Label, TaskFilter.Deleted => "已删除", TaskFilter.Completed => "已完成", TaskFilter.Open => "未完成", TaskFilter.Month => "本月", TaskFilter.Week => "本周", _ => "今天" };
    public string DateSubtitle => DateTime.Now.ToString("yyyy 年 M 月 d 日  ·  dddd");
    public bool IsCompletedTab => Filter == TaskFilter.Completed;
    private bool _busy;
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }
    private bool _loading;
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    private string _message = "把想做的事写下来，让每一天轻一点。";
    public string Message { get => _message; set => Set(ref _message, value); }
    public bool IsEmpty => HasBook && Tasks.Count == 0 && !IsLoading;
    public string LoadedLabel => HasBook ? $"已载入 {Tasks.Count} 条" + (HasOlder || HasNewer ? " · 滚动翻阅" : "") : "一本书，装下你的小目标";

    public MainViewModel(BookLibrary library, AppSettings settings) { Library = library; Settings = settings; }
    public async Task InitializeAsync()
    {
        await RefreshBooksAsync();
        var book = Books.FirstOrDefault(b => b.Name.Equals(Settings.LastBook, StringComparison.OrdinalIgnoreCase)) ?? Books.FirstOrDefault();
        if (book != null) await OpenBookAsync(book);
        if (Library.ScanWarning != null) Message = "部分待办书未能打开：" + Library.ScanWarning;
    }
    public async Task RefreshBooksAsync()
    {
        var rows = await Library.ListAsync(); Books.Clear(); foreach (var b in rows) Books.Add(b);
    }
    public async Task OpenBookAsync(BookInfo book)
    {
        CancelQuery(); CurrentBook = book; Repository = new(book.Path); CurrentProjectId = null; Filter = TaskFilter.Today;
        Sort = await Repository.GetSettingAsync("CompletedSort") == "Created" ? TaskSort.Created : TaskSort.Completed;
        Settings.LastBook = book.Name; Library.SaveSettings(Settings);
        await RefreshProjectsAsync(); await ReloadAsync(true); Raise(nameof(Sort));
    }
    public async Task RefreshProjectsAsync()
    {
        if (Repository == null) return;
        var projects = await Repository.GetProjectsAsync();
        Projects.Clear(); DraftProjects.Clear();
        var all = new ProjectInfo(null, "全部"); Projects.Add(all); DraftProjects.Add(all);
        foreach (var p in projects) { Projects.Add(p); DraftProjects.Add(p); }
        DraftProjects.Add(new("__new", "新增模块"));
    }
    public async Task SelectProjectAsync(string? id) { CurrentProjectId = id; await ReloadAsync(true); }
    public async Task SelectFilterAsync(TaskFilter filter) { Filter = filter; await ReloadAsync(true); }
    public async Task SelectDatesAsync(DateSelection dates)
    {
        _ = dates.Bounds(); SelectedDates = dates; Filter = TaskFilter.Calendar; await ReloadAsync(true);
    }
    private (long? From, long? Until) QueryRange() => Filter == TaskFilter.Calendar ? SelectedDates.Bounds() : DateRanges.For(Filter, DateTime.Now);
    public DateSelection ReportDates()
    {
        if (Filter == TaskFilter.Calendar) return SelectedDates;
        var today = DateTime.Today;
        return Filter switch
        {
            TaskFilter.Today => new(DateScope.Day, today, today),
            TaskFilter.Month => new(DateScope.Month, today, today),
            TaskFilter.Week => new(DateScope.Range, today.AddDays(-((7 + (int)today.DayOfWeek - 1) % 7)), today.AddDays(6 - ((7 + (int)today.DayOfWeek - 1) % 7))),
            _ => DateSelection.Any
        };
    }
    public async Task SelectSortAsync(TaskSort sort)
    {
        Sort = sort;
        if (Repository != null) await Repository.SetSettingAsync("CompletedSort", sort.ToString());
        await ReloadAsync(true);
    }
    private void CancelQuery() { _epoch++; _queryCancellation.Cancel(); _queryCancellation.Dispose(); _queryCancellation = new(); _paging = false; }
    public async Task ReloadAsync(bool showLatest = false)
    {
        if (!showLatest && Tasks.Count > 0 && Repository != null && CurrentQuery != null)
        { await RefreshWindowAsync(); return; }
        CancelQuery(); Tasks.Clear(); HasOlder = HasNewer = false;
        var epoch = _epoch;
        Raise(nameof(Filter)); Raise(nameof(FilterTitle)); Raise(nameof(IsCompletedTab)); Raise(nameof(IsCalendarFilter)); Raise(nameof(DateSubtitle));
        var range = QueryRange();
        CurrentQuery = new(CurrentProjectId, Filter, Sort, range.From, range.Until);
        await LoadPageAsync(PageDirection.Older); NotifyList();
        if (showLatest && epoch == _epoch) LatestRequested?.Invoke();
    }
    private async Task RefreshWindowAsync()
    {
        var cursor = CurrentQuery!.CursorFor(Tasks[0].Item); var count = Tasks.Count;
        CancelQuery(); var epoch = _epoch; var token = _queryCancellation.Token; var repository = Repository!;
        _paging = true; var completion = _pageCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var range = QueryRange();
        CurrentQuery = new(CurrentProjectId, Filter, Sort, range.From, range.Until);
        var query = CurrentQuery with { Direction = PageDirection.Newer, Cursor = cursor, IncludeCursor = true };
        var rows = new List<TodoItem>(); bool more = false, budgetReached = false; long bytes = 0;
        IsLoading = true;
        try
        {
            do
            {
                query = query with { PageSize = Math.Min(200, count - rows.Count) };
                var page = await repository.QueryAsync(query, token); more = page.HasMore;
                foreach (var item in page.Items)
                {
                    var size = 256L + (item.ContentJson.Length + item.PlainText.Length) * 2L;
                    if (rows.Count > 0 && bytes + size > 32L * 1024 * 1024) { more = true; budgetReached = true; break; }
                    rows.Add(item); bytes += size;
                }
                if (page.Items.Count == 0 || rows.Count >= count || budgetReached) break;
                query = query with { Cursor = query.CursorFor(rows[^1]), IncludeCursor = false };
            } while (more && rows.Count < 2000);
            if (epoch != _epoch) return;
            if (rows.Count == 0) { await ReloadAsync(true); return; }
            var older = await repository.QueryAsync(CurrentQuery with { Cursor = CurrentQuery.CursorFor(rows[0]), PageSize = 1 }, token);
            if (epoch != _epoch) return;
            LayoutChanging?.Invoke(); Tasks.Clear(); foreach (var item in rows) Tasks.Add(MakeRow(item));
            HasOlder = older.Items.Count > 0; HasNewer = more; UpdateHeaders(); LayoutChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        finally { if (epoch == _epoch) { _paging = false; IsLoading = false; NotifyList(); Raise(nameof(DateSubtitle)); } completion.TrySetResult(); }
    }
    public async Task LoadPageAsync(PageDirection direction)
    {
        var requestedEpoch = _epoch;
        while (_paging)
        {
            var pending = _pageCompletion?.Task;
            if (pending == null) return;
            await pending;
            if (requestedEpoch != _epoch) return;
        }
        if (Repository == null || CurrentQuery == null) return;
        if (Tasks.Count > 0 && !(direction == PageDirection.Older ? HasOlder : HasNewer)) return;
        // Retain the active editor and rows with pending transitions until they finish.
        if (Tasks.Count >= 1800 && (Tasks.Any(t => t.IsBusy) || IsEditing?.Invoke() == true)) return;
        _paging = true; IsLoading = true;
        var completion = _pageCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var epoch = _epoch; var token = _queryCancellation.Token;
        var query = CurrentQuery with { Direction = direction, Cursor = Tasks.Count == 0 ? null : CurrentQuery.CursorFor(direction == PageDirection.Older ? Tasks[0].Item : Tasks[^1].Item) };
        try
        {
            var page = await Repository.QueryAsync(query, token);
            if (epoch != _epoch) return;
            LayoutChanging?.Invoke();
            var ids = Tasks.Select(t => t.Id).ToHashSet();
            var rows = page.Items.Where(t => !ids.Contains(t.Id)).Select(MakeRow).ToList();
            if (direction == PageDirection.Older) { for (int i = rows.Count - 1; i >= 0; i--) Tasks.Insert(0, rows[i]); HasOlder = page.HasMore; }
            else { foreach (var row in rows) Tasks.Add(row); HasNewer = page.HasMore; }
            var bytes = Tasks.Sum(t => t.EstimatedBytes);
            while ((Tasks.Count > 2000 || bytes > 32L * 1024 * 1024) && Tasks.Count > 1)
            {
                var index = direction == PageDirection.Older ? Tasks.Count - 1 : 0;
                if (Tasks[index].IsBusy || Tasks[index].IsEditing) break;
                bytes -= Tasks[index].EstimatedBytes; Tasks.RemoveAt(index);
                if (direction == PageDirection.Older) HasNewer = true; else HasOlder = true;
            }
            UpdateHeaders(); LayoutChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        finally { if (epoch == _epoch) { _paging = false; IsLoading = false; NotifyList(); } completion.TrySetResult(); }
    }
    private TaskViewModel MakeRow(TodoItem item) => new(item)
    { SortStamp = CurrentQuery!.SortTime(item), ProjectLabel = Projects.FirstOrDefault(p => p.Id == item.ProjectId)?.Name is { } name && name != "全部" ? name : "" };
    private void UpdateHeaders()
    {
        DateTime? previous = null;
        foreach (var row in Tasks)
        {
            var day = DateTimeOffset.FromUnixTimeMilliseconds(row.SortStamp).LocalDateTime.Date;
            row.HasDateHeader = day != previous;
            row.DateLabel = day == DateTime.Today ? "今天  /  " + day.ToString("M月d日") : day == DateTime.Today.AddDays(-1) ? "昨天  /  " + day.ToString("M月d日") : day.ToString("yyyy年M月d日  dddd");
            previous = day;
        }
    }
    private void NotifyList() { Raise(nameof(IsEmpty)); Raise(nameof(LoadedLabel)); }
    public async Task AddAsync(RichContent content, string? project, string? newProject)
    {
        if (Repository == null) return;
        var repository = Repository;
        var added = await repository.AddTaskAsync(content, project, newProject);
        if (Repository != repository) return;
        if (newProject != null) await RefreshProjectsAsync();
        if (CurrentQuery?.Matches(added) == true) { await ReloadAsync(true); Message = "任务已添加。"; }
        else Message = $"任务已添加到{Projects.FirstOrDefault(p => p.Id == added.ProjectId)?.Name ?? "全部"}模块的未完成列表。";
    }
    public async Task ChangeStatusAsync(TaskViewModel row, bool longPress)
    {
        if (Repository == null || row.IsBusy || row.IsEditing || row.IsDeleted) return;
        var repo = Repository; var epoch = _epoch; var old = row.Item;
        var status = longPress ? (old.Status == TodoStatus.Verification ? TodoStatus.Open : TodoStatus.Verification)
            : old.Status == TodoStatus.Completed ? TodoStatus.Open : TodoStatus.Completed;
        if (longPress && old.Status == TodoStatus.Completed) return;
        row.IsBusy = true;
        row.Item = old with { Status = status, CompletedAt = status == TodoStatus.Completed ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null };
        bool committed = false;
        try
        {
            var saved = await repo.SetStatusAsync(old.Id, status, old.Revision);
            committed = true;
            row.Item = saved;
            if (epoch != _epoch) return;
            if (!CurrentQuery!.Matches(saved))
            {
                if (AnimateRemoval != null) await AnimateRemoval(row);
                if (epoch != _epoch) return;
                Tasks.Remove(row); UpdateHeaders(); NotifyList();
                if (Tasks.Count < 30 && HasOlder) await LoadPageAsync(PageDirection.Older);
            }
            Message = status switch { TodoStatus.Completed => "已完成。", TodoStatus.Verification => "已标记待验证，确认后再打勾。", _ => "已恢复为未完成。" };
        }
        catch { if (!committed) row.Item = old; throw; }
        finally { row.IsBusy = false; }
    }
    public async Task DeleteRestoreAsync(TaskViewModel row)
    {
        if (Repository == null || row.IsBusy || row.IsEditing) return;
        var repo = Repository; var epoch = _epoch; var old = row.Item; var restore = row.IsDeleted;
        row.IsBusy = true; bool committed = false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        row.Item = restore ? old with { Status = old.PreviousStatus!.Value, CompletedAt = old.PreviousCompletedAt, DeletedAt = null, PreviousStatus = null, PreviousCompletedAt = null }
            : old with { Status = TodoStatus.Deleted, DeletedAt = now, PreviousStatus = old.Status, PreviousCompletedAt = old.CompletedAt, CompletedAt = null };
        try
        {
            var saved = restore ? await repo.RestoreTaskAsync(old.Id, old.Revision) : await repo.DeleteTaskAsync(old.Id, old.Revision);
            committed = true; row.Item = saved;
            if (epoch != _epoch) return;
            if (!CurrentQuery!.Matches(saved))
            {
                if (AnimateRemoval != null) await AnimateRemoval(row);
                if (epoch != _epoch) return;
                Tasks.Remove(row); UpdateHeaders(); NotifyList();
                if (Tasks.Count < 30 && HasOlder) await LoadPageAsync(PageDirection.Older);
            }
            Message = restore ? "已恢复至删除前的状态。" : "任务已删除，可通过右键菜单恢复。";
        }
        catch { if (!committed) row.Item = old; throw; }
        finally { row.IsBusy = false; }
    }
    public async Task SaveContentAsync(TaskViewModel row, RichContent content)
    {
        if (Repository == null) return;
        row.Item = await Repository.UpdateContentAsync(row.Id, content, row.Item.Revision);
        row.IsEditing = false; Message = "修改已保存。";
    }
    public void SaveSettings() => Library.SaveSettings(Settings);
}
