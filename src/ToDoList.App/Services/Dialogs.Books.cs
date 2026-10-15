using System.Windows;
using System.Windows.Controls;
using ToDoList.Core;
using Wpf.Ui.Controls;

namespace ToDoList.App.Services;

public static partial class Dialogs
{
    public static async Task<BookInfo?> SelectBook(Window owner, IReadOnlyList<BookInfo> books)
    {
        await Queue.WaitAsync();
        try
        {
            var selector = new System.Windows.Controls.ComboBox { ItemsSource = books, DisplayMemberPath = "Title", SelectedIndex = 0, MinWidth = 320 };
            var panel = new StackPanel();
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "选择目标待办笔记；全部任务、回收站及状态历史会一起迁移。", TextWrapping = TextWrapping.Wrap, MaxWidth = 400, Margin = new(0, 0, 0, 12) });
            panel.Children.Add(selector);
            // Show the ID so equal display titles still have an unambiguous destination.
            var identity = new System.Windows.Controls.TextBlock { Margin = new(0, 8, 0, 0), FontSize = 11 };
            void Describe() => identity.Text = (selector.SelectedItem as BookInfo)?.BookId ?? "";
            selector.SelectionChanged += (_, _) => Describe(); Describe(); panel.Children.Add(identity);
            var dialog = Create(owner, "迁移模块", panel); dialog.PrimaryButtonText = "迁移";
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? selector.SelectedItem as BookInfo : null;
        }
        finally { Queue.Release(); }
    }
}
