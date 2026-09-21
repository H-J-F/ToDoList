using System.Windows;
using System.Windows.Controls;
using ToDoList.App.Controls;
using ToDoList.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ToDoList.App.Services;

internal static class ReportDialog
{
    public static async Task<ReportOptions?> ShowAsync(MainWindow owner)
    {
        var model = owner.Model;
        var panel = new StackPanel { MinWidth = 340, MaxWidth = 420 };
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new(0, 10, 0, 6) });
        Label("模块");
        var projects = new ComboBox { ItemsSource = model.Projects.ToArray(), DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedIndex = 0 };
        projects.SelectedItem = model.Projects.FirstOrDefault(p => p.Id == model.CurrentProjectId); panel.Children.Add(projects);
        Label("任务状态");
        var states = new[] { TaskFilter.All, TaskFilter.Deleted, TaskFilter.Completed, TaskFilter.Open };
        var status = new ComboBox { ItemsSource = new[] { "所有", "已删除", "已完成", "未完成（含待验证）" }, SelectedIndex = Math.Max(0, Array.IndexOf(states, model.Filter)) }; panel.Children.Add(status);
        Label("添加时间"); var dates = new DateRangePicker(false, model.ReportDates()); panel.Children.Add(dates);
        Label("输出格式");
        var format = new ComboBox { ItemsSource = new[] { "Markdown 文件 (.md)", "Word 文档 (.docx)", "PDF 文件 (.pdf)" }, SelectedIndex = 0 }; panel.Children.Add(format);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) }; error.SetResourceReference(TextBlock.ForegroundProperty, "RedBrush"); panel.Children.Add(error);
        var dialog = new ContentDialog(owner.DialogPresenter) { Title = "导出报告", Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(220, owner.ActualHeight - 230), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, PrimaryButtonText = "选择保存位置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        ReportOptions? result = null;
        dialog.Closing += (sender, e) =>
        {
            if (e.Result != ContentDialogResult.Primary) return;
            try { result = new((projects.SelectedItem as ProjectInfo)?.Id, states[status.SelectedIndex], dates.Value, (ReportFormat)format.SelectedIndex); _ = result.Query(); }
            catch (ArgumentException ex) { error.Text = ex.Message; e.Cancel = true; }
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }
}
