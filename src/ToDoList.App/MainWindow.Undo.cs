using System.Windows;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.App;

public partial class MainWindow
{
    private LocalUndo? _undo;
    private bool _undoInProgress;
    private void InitializeUndo()
    {
        Notifications.IsUndoAvailable = () => !Model.IsBusy && Model.Repository?.IsWriting != true && _pendingMutations == 0 && !_navigating && !_cloudSwitching;
        Model.OperationCommitted += undo => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closingApproved || !IsVisible || Model.Repository?.Path != undo.Path) return;
            _feedbackTimer.Stop(); _undo = undo;
            Notifications.Show(undo.Message, true, _cloudBusy);
        }));
        Notifications.Dismissed += (_, _) => _undo = null;
        Notifications.UndoRequested += async (_, _) => await UndoLastOperationAsync();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Notifications.Dismiss(); };
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Model.CurrentBook) && _undo?.Path != Model.Repository?.Path) Notifications.Dismiss();
        };
    }
    private async Task UndoLastOperationAsync()
    {
        var undo = _undo;
        if (undo == null || _undoInProgress || _pendingMutations > 0 || _navigating || _cloudSwitching || Model.IsBusy || !Notifications.AcceptUndo()) return;
        var enteredSync = Notifications.Clock.EnteredSync;
        var committed = false;
        _undo = null; _undoInProgress = true; _pendingMutations++; Model.IsBusy = true;
        try
        {
            await undo.UndoAsync();
            committed = true;
            _cloudSchedule.Changed(DateTime.UtcNow);
            while (_cloudApplying) await Task.Delay(20);
            var id = Model.CurrentBook!.BookId;
            var changes = new CloudChangeSet(undo.Records.Select(row => CloudSnapshot.Key(id, row.Kind, row.Id)).ToHashSet());
            var previous = _sync; _sync = true;
            try { await Model.RefreshAfterCloudAsync(changes); ClearDeletedSelection(); SyncSelectors(); }
            finally { _sync = previous; }
            Notifications.Show("已撤回。");
            if (_cloudAccount != null) SetCloudStatus(enteredSync ? "撤回已保存 · 等待同步" : "已撤回本次操作");
        }
        catch (Exception ex) { ToDoList.Core.RuntimeLog.Write("undo.failed", error: ex); Notifications.Show((committed ? "已撤回，界面刷新未完成：" : "撤回未完成：") + ex.Message); }
        finally { _undoInProgress = false; _pendingMutations--; Model.IsBusy = false; }
    }
}
