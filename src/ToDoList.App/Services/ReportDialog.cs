using System.Windows;
using System.Windows.Controls;
using ToDoList.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ToDoList.App.Services;

internal static class ReportDialog
{
    public static async Task<ReportOptions?> ShowAsync(MainWindow owner)
    {
        var model = owner.Model;
        var panel = new Grid { MinWidth = 420, MaxWidth = 460 };
        for (int i = 0; i < 8; i++) panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var row = 0;
        void Add(string label, FrameworkElement control)
        {
            var caption = new TextBlock { Text = label, Margin = new(0, row == 0 ? 0 : 10, 0, 6) };
            Grid.SetRow(caption, row++); panel.Children.Add(caption);
            control.HorizontalAlignment = HorizontalAlignment.Stretch; Grid.SetRow(control, row++); panel.Children.Add(control);
        }
        var projects = new ComboBox { ItemsSource = model.Projects.ToArray(), DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedIndex = 0 };
        projects.SelectedItem = model.Projects.FirstOrDefault(p => p.Id == model.CurrentProjectId); Add("模块", projects);
        var states = new[] { TaskFilter.All, TaskFilter.Deleted, TaskFilter.Completed, TaskFilter.Open };
        var status = new ComboBox { ItemsSource = new[] { "所有", "已删除", "已完成", "未完成（含待验证）" }, SelectedIndex = Math.Max(0, Array.IndexOf(states, model.Filter)) }; Add("任务状态", status);
        var dates = new DateSelectionForm(false, model.ReportDates()); Add("添加时间", dates.View);
        var format = new ComboBox { ItemsSource = new[] { "Markdown 文件 (.md)", "Word 文档 (.docx)", "PDF 文件 (.pdf)" }, SelectedIndex = 0 }; Add("输出格式", format);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) }; error.SetResourceReference(TextBlock.ForegroundProperty, "RedBrush"); panel.Children.Add(error);
        Grid.SetRow(error, row); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var dialog = new ContentDialog(owner.DialogPresenter) { Title = "导出报告", Content = panel, PrimaryButtonText = "选择保存位置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
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
