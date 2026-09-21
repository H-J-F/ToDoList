using ToDoList.Core;

namespace ToDoList.App.ViewModels;

public sealed class TaskViewModel(TodoItem item) : ObservableObject
{
    private TodoItem _item = item;
    public TodoItem Item { get => _item; set { _item = value; Raise(); Raise(nameof(IsCompleted)); Raise(nameof(IsVerification)); Raise(nameof(IsDeleted)); Raise(nameof(CheckState)); Raise(nameof(StateHint)); Raise(nameof(CompletionLabel)); Raise(nameof(CreationLabel)); Raise(nameof(TimeDetails)); } }
    public string Id => Item.Id;
    public bool IsCompleted => Item.Status == TodoStatus.Completed;
    public bool IsVerification => Item.Status == TodoStatus.Verification;
    public bool IsDeleted => Item.Status == TodoStatus.Deleted;
    public bool? CheckState => IsVerification ? null : IsCompleted;
    public string CreationLabel => "添加于 " + FormatTime(Item.CreatedAt);
    public string TimeDetails => "添加：" + DateTimeOffset.FromUnixTimeMilliseconds(Item.CreatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        + (Item.CompletedAt is long completed ? "\n完成：" + DateTimeOffset.FromUnixTimeMilliseconds(completed).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "")
        + (Item.DeletedAt is long deleted ? "\n删除：" + DateTimeOffset.FromUnixTimeMilliseconds(deleted).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "");
    private static string FormatTime(long time)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(time).ToLocalTime();
        return local.ToString(local.Year == DateTime.Now.Year ? "MM月dd日 HH:mm" : "yyyy年MM月dd日 HH:mm");
    }
    public string StateHint => IsDeleted ? "已删除 · 右键恢复" : IsCompleted ? "已完成 · 单击恢复未完成" : IsVerification ? "待验证 · 单击完成，长按取消待验证" : "单击完成 · 长按标记待验证";
    public string CompletionLabel => IsDeleted ? "已删除 · " + FormatTime(Item.DeletedAt!.Value) : Item.CompletedAt.HasValue ? "完成于 " + FormatTime(Item.CompletedAt.Value) : "";
    private bool _busy;
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }
    private bool _editing;
    public bool IsEditing { get => _editing; set => Set(ref _editing, value); }
    private bool _header;
    public bool HasDateHeader { get => _header; set => Set(ref _header, value); }
    private string _date = "";
    public string DateLabel { get => _date; set => Set(ref _date, value); }
    public string ProjectLabel { get; set; } = "";
    public long SortStamp { get; set; }
    public long EstimatedBytes => 256L + (Item.ContentJson.Length + Item.PlainText.Length) * 2L;
}
