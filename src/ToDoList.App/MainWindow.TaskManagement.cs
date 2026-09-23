using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ToDoList.App.Services;
using ToDoList.App.ViewModels;
using ToDoList.Core;

namespace ToDoList.App;

public partial class MainWindow
{
    private readonly Dictionary<string, long> _deletedSelection = [];
    private bool _selectingDeleted;
    private readonly DispatcherTimer _projectHold = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(500) };
    private long _projectPressedAt;
    private ProjectInfo? _dragProject;
    private ListBoxItem? _dropTarget;
    private Point _projectPress;
    private bool _dropAfter;
    private ProjectDragPreview? _projectPreview;

    private void InitializeTaskManagement()
    {
        Model.Tasks.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (TaskViewModel row in e.NewItems)
            { row.SelectionMode = _selectingDeleted && row.IsDeleted; row.SelectedForDeletion = _deletedSelection.ContainsKey(row.Id); }
        };
        ProjectList.PreviewMouseLeftButtonDown += (_, e) =>
        {
            CancelProjectPress();
            if (_navigating || Model.IsBusy || Model.IsReorderingProjects) return;
            var item = FindProjectContainer(e.OriginalSource as DependencyObject);
            if (item?.DataContext is not ProjectInfo { Id: not null } project) return;
            // ListBox's own mouse capture/selection must not consume the hold gesture.
            e.Handled = true;
            ProjectList.Focus();
            _dragProject = project; _projectPress = e.GetPosition(ProjectList); _projectPressedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (ProjectList.CaptureMouse()) _projectHold.Start();
            else CancelProjectPress();
        };
        _projectHold.Tick += (_, _) =>
        {
            _projectHold.Stop();
            if (_dragProject == null || Mouse.LeftButton != MouseButtonState.Pressed) { CancelProjectPress(); return; }
            BeginProjectDrag();
        };
        ProjectList.PreviewMouseMove += (_, e) =>
        {
            if (_dragProject == null) return;
            var position = e.GetPosition(ProjectList);
            if (!_projectDragging)
            {
                // Input can arrive before a busy dispatcher's timer callback.
                if (System.Diagnostics.Stopwatch.GetElapsedTime(_projectPressedAt).TotalMilliseconds >= 500)
                { _projectHold.Stop(); BeginProjectDrag(); }
                else { if ((position - _projectPress).Length > 8) CancelProjectPress(); return; }
            }
            e.Handled = true;
            UpdateProjectDropTarget(position);
        };
        ProjectList.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            if (_dragProject == null) return;
            e.Handled = true;
            await CompleteProjectPressAsync(e.GetPosition(ProjectList));
        };
        ProjectList.LostMouseCapture += (_, _) => CancelProjectPress();
        ProjectList.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _dragProject != null) { e.Handled = true; CancelProjectPress(); } };
        ProjectList.PreviewMouseWheel += (_, _) => CancelProjectPress();
        Deactivated += (_, _) => CancelProjectPress();
        LocationChanged += (_, _) => QueueWindowPlacement();
        SizeChanged += (_, _) => QueueWindowPlacement();
        StateChanged += (_, _) => QueueWindowPlacement();
        Closed += (_, _) => CancelProjectPress();
        ProjectList.SizeChanged += (_, _) => CancelProjectPress();
        Model.Projects.CollectionChanged += (_, _) => CancelProjectPress();
    }
    private bool _projectDragging;
    private ListBoxItem? FindProjectContainer(DependencyObject? element) =>
        element == null ? null : ItemsControl.ContainerFromElement(ProjectList, element) as ListBoxItem;

    private void BeginProjectDrag()
    {
        if (_projectDragging || _dragProject == null) return;
        var source = Model.Projects.FirstOrDefault(p => p.Id == _dragProject.Id);
        if (source == null || ProjectList.ItemContainerGenerator.ContainerFromItem(source) is not ListBoxItem item) { CancelProjectPress(); return; }
        _projectPreview = new ProjectDragPreview(ProjectList, item, _projectPress);
        _projectDragging = true; ProjectList.Cursor = Cursors.SizeAll;
    }

    private void UpdateProjectDropTarget(Point position)
    {
        if (_dragProject == null || !_projectDragging || _projectPreview == null) return;
        _projectPreview.Update(position);
        _dropTarget = _projectPreview.Target; _dropAfter = _projectPreview.After;
    }
    private async Task CompleteProjectPressAsync(Point position)
    {
        // Recheck release coordinates even if the final mouse move was coalesced.
        if (_projectDragging) UpdateProjectDropTarget(position);
        if (_dragProject == null) return;
        var source = _dragProject; var target = _dropTarget?.DataContext as ProjectInfo;
        var after = _dropAfter; var dragging = _projectDragging;
        CancelProjectPress();
        if (dragging && target != null) await SafeAsync(() => MoveProjectToAsync(source, target, after));
        else if (!dragging) ProjectList.SelectedItem = Model.Projects.FirstOrDefault(p => p.Id == source.Id);
    }
    private void CancelProjectPress()
    {
        _projectHold.Stop(); _dragProject = null; _projectDragging = false; _dropTarget = null;
        _projectPreview?.Dispose(); _projectPreview = null;
        ProjectList.ClearValue(CursorProperty);
        if (ProjectList.IsMouseCaptured) ProjectList.ReleaseMouseCapture();
    }
    private void QueueWindowPlacement()
    {
        if (!_initialized || WindowState == WindowState.Minimized) return;
        var bounds = RestoreBounds;
        if (bounds.IsEmpty) return;
        var s = Model.Settings;
        s.Width = bounds.Width; s.Height = bounds.Height; s.Left = bounds.Left; s.Top = bounds.Top; s.Maximized = WindowState == WindowState.Maximized;
        _settingsSave.Stop(); _settingsSave.Start();
    }
    internal async Task MoveProjectToAsync(ProjectInfo source, ProjectInfo target, bool after)
    {
        if (Model.Repository == null || Model.IsBusy || source.Id == null || target.Id == null || source.Id == target.Id) return;
        var rows = Model.Projects.Where(p => p.Id != null).Select(p => p.Id!).ToList();
        if (!rows.Remove(source.Id) || !rows.Contains(target.Id)) return;
        rows.Insert(rows.IndexOf(target.Id) + (after ? 1 : 0), source.Id);
        await Model.SetProjectOrderAsync(rows);
    }
    internal void StartDeletedSelection(TaskViewModel row)
    {
        if (Model.Filter != TaskFilter.Deleted || !row.IsDeleted) return;
        _selectingDeleted = true; row.SelectedForDeletion = true; UpdateDeletedSelection(row);
        foreach (var item in Model.Tasks) item.SelectionMode = item.IsDeleted;
        DeletedActions.Visibility = Visibility.Visible;
    }
    internal void UpdateDeletedSelection(TaskViewModel row)
    {
        if (!_selectingDeleted) return;
        if (row.SelectedForDeletion && row.IsDeleted) _deletedSelection.TryAdd(row.Id, row.Item.Revision);
        else _deletedSelection.Remove(row.Id);
        DeletedSelectionCount.Text = $"已选 {_deletedSelection.Count} 项";
        DeleteSelectedButton.IsEnabled = _deletedSelection.Count > 0;
    }
    private void ClearDeletedSelection()
    {
        _selectingDeleted = false; _deletedSelection.Clear(); DeletedActions.Visibility = Visibility.Collapsed;
        foreach (var row in Model.Tasks) { row.SelectionMode = false; row.SelectedForDeletion = false; }
    }
    private void CancelDeletedSelection_Click(object sender, RoutedEventArgs e) => ClearDeletedSelection();
    private async void DeleteSelected_Click(object sender, RoutedEventArgs e) => await DeletePermanentlyAsync(new Dictionary<string, long>(_deletedSelection));
    internal async Task DeletePermanentlyAsync(IReadOnlyDictionary<string, long> revisions)
    {
        if (Model.IsBusy || Model.Filter != TaskFilter.Deleted || revisions.Count == 0 || Model.Repository == null) return;
        Model.IsBusy = true;
        try
        {
            var repo = Model.Repository;
            var choice = await Dialogs.Choose(this, "彻底删除任务", $"确定彻底删除选中的 {revisions.Count} 项任务及其状态历史吗？\n\n此操作不可恢复。", "取消", "彻底删除");
            if (choice != 1) return;
            await repo.PermanentlyDeleteAsync(revisions); ClearDeletedSelection(); await Model.ReloadAsync(true);
            Model.Message = $"已彻底删除 {revisions.Count} 项任务。";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { Model.IsBusy = false; }
    }
}
