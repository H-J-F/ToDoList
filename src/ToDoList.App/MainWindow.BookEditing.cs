using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ToDoList.App.Controls;
using ToDoList.App.Services;
using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.App;

public partial class MainWindow
{
    private TaskCard? _failedAutoSaveCard;
    private bool _focusSaveQueued;
    private ProjectInfo? _contextProject;
    private void InitializeRowFocus()
    {
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => QueueFocusSave()), true);
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) =>
        { if (_editingCard?.IsKeyboardFocusWithin == true) _failedAutoSaveCard = null; }), true);
        Deactivated += (_, _) => QueueFocusSave();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) QueueFocusSave(); };
        PreviewMouseDown += (_, e) =>
        {
            if (IsWithin(e.OriginalSource as DependencyObject, Notifications)) return;
            if (_editingCard != null && !IsWithin(e.OriginalSource as DependencyObject, _editingCard) && _editingCard.ActiveEditor?.HasToolInteraction != true)
                QueueFocusSave(true);
        };
        ProjectList.PreviewMouseRightButtonDown += (_, e) =>
        {
            var item = FindProjectContainer(e.OriginalSource as DependencyObject);
            _contextProject = item?.DataContext as ProjectInfo;
        };
        ProjectList.ContextMenu.Closed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => _contextProject = null));
        ProjectList.ContextMenu.Opened += (_, _) =>
        {
            var enabled = SelectedRealProject() != null && !Model.IsBusy && !_cloudApplying && !_cloudSwitching;
            foreach (var item in ProjectList.ContextMenu.Items.OfType<MenuItem>().Where(i => (string?)i.Header != "新增模块")) item.IsEnabled = enabled;
        };
    }
    private static bool IsWithin(DependencyObject? node, DependencyObject root)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, root)) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
    private void QueueFocusSave(bool outsidePointer = false)
    {
        if (_focusSaveQueued) return;
        var card = _editingCard;
        if (card == null) { if (!DraftEditor.IsComposing) _cloudSchedule.EditingEnded(DateTime.UtcNow); return; }
        if (_savingRow || card.IsExpandingEdit || card == _failedAutoSaveCard) return;
        _focusSaveQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () =>
        {
            _focusSaveQueued = false;
            if (_editingCard != card || _savingRow || _navigating || _undoInProgress || Notifications.IsMouseOver || IsWithin(Keyboard.FocusedElement as DependencyObject, Notifications) || card.ActiveEditor?.HasToolInteraction == true || card.ActiveProjectSelector?.IsDropDownOpen == true) return;
            if (!outsidePointer && IsActive && WindowState != WindowState.Minimized && card.IsKeyboardFocusWithin) return;
            try { if (card.HasPendingChanges) await SaveRowEditAsync(); else EndRowEdit(); }
            catch (Exception ex) { _failedAutoSaveCard = card; ShowError(ex); }
        }));
    }
    private async void RenameBook_Click(object sender, RoutedEventArgs e)
    {
        if (Model.IsBusy || _cloudApplying || Model.CurrentBook is not { } book || !await TryLeaveRowEditAsync()) return;
        var title = await Dialogs.Input(this, "重命名待办笔记", "新的展示标题", book.Title);
        if (title == null) return;
        await SafeAsync(async () =>
        {
            await BookOperations.RenameAsync(Model.Library, book, title);
            await Model.RefreshAfterCloudAsync(new(new HashSet<string> { Storage.Cloud.CloudSnapshot.Key(book.BookId, "book", book.BookId) }));
            SyncSelectors(); Model.Message = "待办笔记已重命名。";
        });
    }
    private async void MigrateProject_Click(object sender, RoutedEventArgs e)
    {
        var project = SelectedRealProject();
        if (project?.Id == null || Model.CurrentBook is not { } source || !await TryLeaveEditsAsync()) return;
        var targets = (await Model.Library.ListAsync()).Where(b => b.BookId != source.BookId).ToArray();
        if (targets.Length == 0) { await Dialogs.Info(this, "没有可迁移的目标", "请先创建另一待办笔记。"); return; }
        var target = await Dialogs.SelectBook(this, targets);
        if (target == null) return;
        var projects = await new TaskRepository(target.Path).GetProjectsAsync();
        var sameName = projects.FirstOrDefault(p => p.Name.Equals(project.Name, StringComparison.OrdinalIgnoreCase));
        string? newName = null, mergeId = null;
        if (sameName != null)
        {
            var choice = await Dialogs.Choose(this, "目标已有同名模块", $"“{target.Title}”中已有模块“{project.Name}”。请选择合并，或改名后迁移。", "取消", "合并同名模块", "改名后迁移");
            if (choice == 0) return;
            if (choice == 1) mergeId = sameName.Id;
            else { newName = await Dialogs.Input(this, "改名后迁移", "新的模块名称", project.Name); if (newName == null) return; }
        }
        _cloudSwitching = true;
        try
        {
            await StopCloudAsync();
            await SafeAsync(async () =>
            {
                var changes = await BookOperations.MigrateProjectAsync(Model.Library, source, target, project.Id, newName, mergeId, ConflictCoordinator.Conflicts);
                var prior = _sync; _sync = true;
                try { await Model.RefreshAfterCloudAsync(changes); SyncSelectors(); }
                finally { _sync = prior; }
                Model.Message = $"模块已迁移到“{target.Title}”。";
            });
        }
        finally { _cloudSwitching = false; UpdateCloudControls(); }
    }
}
