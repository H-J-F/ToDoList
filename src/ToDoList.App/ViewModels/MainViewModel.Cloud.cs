using System.Collections.ObjectModel;
using ToDoList.Core;
using ToDoList.Storage.Cloud;

namespace ToDoList.App.ViewModels;

public sealed partial class MainViewModel
{
    private BookInfo? RememberedBook() => VisibleBooks.FirstOrDefault(b => b.BookId == Settings.LastBook)
        ?? VisibleBooks.FirstOrDefault(b => b.Name.Equals(Settings.LastBook, StringComparison.OrdinalIgnoreCase)) ?? VisibleBooks.FirstOrDefault();
    private static void Reconcile<T>(ObservableCollection<T> collection, IReadOnlyList<T> desired, Func<T, string?> key)
    {
        var wanted = desired.Select(key).ToHashSet();
        for (var i = collection.Count - 1; i >= 0; i--) if (!wanted.Contains(key(collection[i]))) collection.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var index = -1;
            for (var j = i; j < collection.Count; j++) if (key(collection[j]) == key(desired[i])) { index = j; break; }
            if (index < 0) collection.Insert(i, desired[i]);
            else
            {
                if (index != i) collection.Move(index, i);
                if (!EqualityComparer<T>.Default.Equals(collection[i], desired[i])) collection[i] = desired[i];
            }
        }
    }
    private void ReconcileTasks(IReadOnlyList<TodoItem> items)
    {
        var existing = Tasks.ToDictionary(t => t.Id);
        var desired = items.Select(item =>
        {
            if (!existing.TryGetValue(item.Id, out var row)) return MakeRow(item);
            if (row.Item != item) row.Item = item;
            row.SortStamp = CurrentQuery!.SortTime(item);
            row.ProjectLabel = Projects.FirstOrDefault(p => p.Id == item.ProjectId && p.Id != null)?.Name ?? "";
            return row;
        }).ToArray();
        Reconcile(Tasks, desired, t => t.Id);
    }
    internal async Task RefreshAfterCloudAsync(CloudChangeSet? changes = null)
    {
        var id = CurrentBook?.BookId;
        if (changes is { HasChanges: false }) return;
        if (changes == null || changes.Keys.Any(k => CloudSnapshot.Kind(k) == "book" ||
            CloudSnapshot.Kind(k) == "setting" && CloudSnapshot.Id(k) == BookInfo.HiddenSetting)) await RefreshBooksAsync();
        var book = Books.FirstOrDefault(b => b.BookId == id);
        if (book == null || book.IsHidden && !ShowHiddenBooks)
        {
            if (VisibleBooks.FirstOrDefault() is { } next)
            {
                try { await OpenBookAsync(next); }
                catch
                {
                    if (book != null) { CurrentBook = book; SetShowHiddenBooks(true); }
                    throw;
                }
            }
            else
            {
                CancelQuery(); Repository = null; CurrentBook = null; CurrentProjectId = null; CurrentQuery = null;
                Reconcile(Tasks, Array.Empty<TaskViewModel>(), t => t.Id);
                Reconcile(Projects, Array.Empty<ProjectInfo>(), p => p.Id);
                Reconcile(DraftProjects, Array.Empty<ProjectInfo>(), p => p.Id);
                HasOlder = HasNewer = false; Settings.LastBook = null; SaveSettings(); NotifyList();
            }
            if (id != null) Message = book == null ? "当前待办笔记已删除，已切换到可用笔记。" : "当前待办笔记已隐藏。";
            return;
        }
        if (CurrentBook != book) CurrentBook = book;
        if (changes != null && !changes.Affects(book.BookId)) return;
        var projectChanged = changes == null || changes.HasKind(book.BookId, "project");
        var settingChanged = changes == null || changes.HasKind(book.BookId, "setting");
        var filterChanged = false;
        if (projectChanged)
        {
            var projects = await Repository!.GetProjectsAsync();
            var all = new ProjectInfo(null, "全部");
            Reconcile(Projects, new[] { all }.Concat(projects).ToArray(), p => p.Id);
            Reconcile(DraftProjects, new[] { all }.Concat(projects).Append(new("__new", "新增模块")).ToArray(), p => p.Id);
            if (CurrentProjectId != null && !projects.Any(p => p.Id == CurrentProjectId))
            { CurrentProjectId = null; filterChanged = true; Message = "当前模块已删除或迁移，已显示全部模块。"; }
            foreach (var row in Tasks) row.ProjectLabel = projects.FirstOrDefault(p => p.Id == row.Item.ProjectId)?.Name ?? "";
        }
        if (settingChanged)
        {
            var sort = await Repository!.GetSettingAsync("CompletedSort") == "Created" ? TaskSort.Created : TaskSort.Completed;
            filterChanged |= sort != Sort; Sort = sort; Raise(nameof(Sort));
            ReportOpenColor = await Repository.GetSettingAsync("StatusColor.Open");
            ReportVerificationColor = await Repository.GetSettingAsync("StatusColor.Verification");
            ReportCompletedColor = await Repository.GetSettingAsync("StatusColor.Completed");
        }
        if (filterChanged || changes == null || changes.HasKind(book.BookId, "task"))
        {
            if (Tasks.Count > 0) await RefreshWindowAsync(!HasNewer && changes != null ? changes.Keys.Count(k => CloudSnapshot.Book(k) == book.BookId && CloudSnapshot.Kind(k) == "task") : 0);
            else
            {
                CancelQuery(); var range = QueryRange(); CurrentQuery = new(CurrentProjectId, Filter, Sort, range.From, range.Until);
                await LoadPageAsync(PageDirection.Older);
            }
        }
    }
}
