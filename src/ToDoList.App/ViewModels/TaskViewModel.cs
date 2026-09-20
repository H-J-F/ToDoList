using ToDoList.Core;

namespace ToDoList.App.ViewModels;

public sealed class TaskViewModel(TodoItem item) : ObservableObject
{
    private TodoItem _item = item;
    public TodoItem Item { get => _item; set { _item = value; Raise(); Raise(nameof(IsCompleted)); Raise(nameof(IsVerification)); Raise(nameof(StateGlyph)); Raise(nameof(StateHint)); Raise(nameof(CompletionLabel)); } }
    public string Id => Item.Id;
    public bool IsCompleted => Item.Status == TodoStatus.Completed;
    public bool IsVerification => Item.Status == TodoStatus.Verification;
    public string StateGlyph => IsCompleted ? "✓" : IsVerification ? "•" : "";
    public string StateHint => IsCompleted ? "已完成 · 单击恢复未完成" : IsVerification ? "待验证 · 单击完成，长按取消待验证" : "单击完成 · 长按标记待验证";
    public string CompletionLabel => Item.CompletedAt.HasValue ? "完成于 " + DateTimeOffset.FromUnixTimeMilliseconds(Item.CompletedAt.Value).ToLocalTime().ToString("MM月dd日 HH:mm") : "";
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
