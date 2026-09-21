using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace ToDoList.App.Services;
public static class Dialogs
{
    private static string _bookName = "", _bookTitle = "";
    public static void ClearBookDraft() { _bookName = _bookTitle = ""; }
    private static readonly SemaphoreSlim Queue = new(1, 1);
    private static ContentDialog Create(Window? owner, string title, object content) => new(((MainWindow)(owner ?? Application.Current.MainWindow)).DialogPresenter)
    { Title = title, Content = content, CloseButtonText = "取消", PrimaryButtonText = "确定", DefaultButton = ContentDialogButton.Primary };
    public static async Task<string?> Input(Window? owner, string title, string label, string initial = "")
    {
        await Queue.WaitAsync();
        try
        {
            var input = new TextBox { Text = initial, MinWidth = 300, Margin = new(0, 10, 0, 0) };
            var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(input);
            var dialog = Create(owner, title, panel); dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
        }
        finally { Queue.Release(); }
    }
    public static async Task<(string Name, string Title)?> CreateBook(Window owner)
    {
        await Queue.WaitAsync();
        try
        {
            var panel = new StackPanel { MinWidth = 320 };
            panel.Children.Add(new TextBlock { Text = "标识名 · 1–64 个英文字母或数字" });
            var name = new TextBox { Text = _bookName, MaxLength = 64, Margin = new(0, 8, 0, 16) }; panel.Children.Add(name);
            panel.Children.Add(new TextBlock { Text = "展示标题" });
            var title = new TextBox { Text = _bookTitle, MaxLength = 100, Margin = new(0, 8, 0, 12) }; panel.Children.Add(title);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; error.SetResourceReference(TextBlock.ForegroundProperty, "RedBrush"); panel.Children.Add(error);
            name.TextChanged += (_, _) => { _bookName = name.Text; error.Text = ""; };
            title.TextChanged += (_, _) => { _bookTitle = title.Text; error.Text = ""; };
            var dialog = Create(owner, "创建待办书", panel); dialog.PrimaryButtonText = "创建";
            dialog.Closing += (_, e) =>
            {
                if (e.Result != ContentDialogResult.Primary) return;
                try
                {
                    Core.BookRules.ValidateName(name.Text); Core.BookRules.ValidateTitle(title.Text);
                    if (((MainWindow)owner).Model.Books.Any(b => b.Name.Equals(name.Text, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException("已有相同标识名的待办书。");
                }
                catch (ArgumentException ex) { error.Text = ex.Message; e.Cancel = true; }
            };
            dialog.Loaded += (_, _) => name.Focus();
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? (name.Text, title.Text.Trim()) : null;
        }
        finally { Queue.Release(); }
    }
    public static async Task<int> Choose(Window? owner, string title, string message, params string[] choices)
    {
        await Queue.WaitAsync();
        try
        {
            var dialog = Create(owner, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 430 });
            dialog.CloseButtonText = choices[0]; dialog.PrimaryButtonText = choices.Length > 1 ? choices[^1] : "";
            dialog.SecondaryButtonText = choices.Length > 2 ? choices[1] : "";
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? choices.Length - 1 : result == ContentDialogResult.Secondary ? 1 : 0;
        }
        finally { Queue.Release(); }
    }
    public static async Task Info(Window? owner, string title, string message) => await Choose(owner, title, message, "知道了");
}
