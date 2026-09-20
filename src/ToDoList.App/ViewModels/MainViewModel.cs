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
    public Func<TaskViewModel, Task>? AnimateRemoval { get; set; }
    public Func<bool>? IsEditing { get; set; }
    private BookInfo? _book;
    public BookInfo? CurrentBook { get => _book; private set { Set(ref _book, value); Raise(nameof(HasBook)); Raise(nameof(BookTitle)); } }
    public bool HasBook => CurrentBook != null;
    public string BookTitle => CurrentBook?.Title ?? "我的第一本待办书";
    public string? CurrentProjectId { get; private set; }
    public TaskFilter Filter { get; private set; } = TaskFilter.Today;
    public TaskSort Sort { get; private set; } = TaskSort.Completed;
    public string FilterTitle => Filter switch { TaskFilter.Completed => "完成的小成就", TaskFilter.Open => "慢慢来，一件一件做", TaskFilter.Month => "这个月的计划", TaskFilter.Week => "这一周的小目标", _ => "今天，也要好好生活" };
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
        await RefreshProjectsAsync(); await ReloadAsync(); Raise(nameof(Sort));
    }
    public async Task RefreshProjectsAsync()
    {
        if (Repository == null) return;
        var projects = await Repository.GetProjectsAsync();
        Projects.Clear(); DraftProjects.Clear();
        var all = new ProjectInfo(null, "全部"); Projects.Add(all); DraftProjects.Add(all);
        foreach (var p in projects) { Projects.Add(p); DraftProjects.Add(p); }
        DraftProjects.Add(new("__new", "＋ 新增项目"));
    }
    public async Task SelectProjectAsync(string? id) { CurrentProjectId = id; await ReloadAsync(); }
    public async Task SelectFilterAsync(TaskFilter filter) { Filter = filter; await ReloadAsync(); }
    public async Task SelectSortAsync(TaskSort sort)
    {
        Sort = sort;
        if (Repository != null) await Repository.SetSettingAsync("CompletedSort", sort.ToString());
        await ReloadAsync();
    }
    private void CancelQuery() { _epoch++; _queryCancellation.Cancel(); _queryCancellation.Dispose(); _queryCancellation = new(); _paging = false; }
    public async Task ReloadAsync()
    {
        CancelQuery(); Tasks.Clear(); HasOlder = HasNewer = false;
        Raise(nameof(Filter)); Raise(nameof(FilterTitle)); Raise(nameof(IsCompletedTab)); Raise(nameof(DateSubtitle));
        var range = DateRanges.For(Filter, DateTime.Now);
        CurrentQuery = new(CurrentProjectId, Filter, Sort, range.From, range.Until);
        await LoadPageAsync(PageDirection.Older); NotifyList();
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
        var query = CurrentQuery with { Direction = direction, Cursor = Tasks.Count == 0 ? null : CurrentQuery.CursorFor(direction == PageDirection.Older ? Tasks[^1].Item : Tasks[0].Item) };
        try
        {
            var page = await Repository.QueryAsync(query, token);
            if (epoch != _epoch) return;
            LayoutChanging?.Invoke();
            var ids = Tasks.Select(t => t.Id).ToHashSet();
            var rows = page.Items.Where(t => !ids.Contains(t.Id)).Select(MakeRow).ToList();
            if (direction == PageDirection.Older) { foreach (var row in rows) Tasks.Add(row); HasOlder = page.HasMore; }
            else { for (int i = rows.Count - 1; i >= 0; i--) Tasks.Insert(0, rows[i]); HasNewer = page.HasMore; }
            var bytes = Tasks.Sum(t => t.EstimatedBytes);
            while ((Tasks.Count > 2000 || bytes > 32L * 1024 * 1024) && Tasks.Count > 1)
            {
                var index = direction == PageDirection.Older ? 0 : Tasks.Count - 1;
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
        await Repository.AddTaskAsync(content, project, newProject);
        if (newProject != null) await RefreshProjectsAsync();
        await ReloadAsync(); Message = "已记下。给未来的自己一个小小的约定。";
    }
    public async Task ChangeStatusAsync(TaskViewModel row, bool longPress)
    {
        if (Repository == null || row.IsBusy || row.IsEditing) return;
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
            Message = status switch { TodoStatus.Completed => "又完成了一件事，做得真好。", TodoStatus.Verification => "已标记待验证，确认后再打勾。", _ => "已放回未完成，按自己的节奏来。" };
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
