using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using ToDoList.App.Services;
using ToDoList.Core;

namespace ToDoList.App;

public partial class MainWindow
{
    private Popup? _datePopup;
    private bool _exportingReport;
    private void Calendar_Click(object sender, RoutedEventArgs e)
    {
        if (_datePopup?.IsOpen == true) { _datePopup.IsOpen = false; return; }
        var dates = new DateSelectionForm(true, Model.IsCalendarFilter ? Model.SelectedDates : new(DateScope.Day, DateTime.Today, DateTime.Today));
        var panel = new StackPanel { MinWidth = 320, Margin = new(16) };
        panel.Children.Add(new TextBlock { Text = "按添加时间查看", Margin = new(0, 0, 0, 10) }); panel.Children.Add(dates.View);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 280, Margin = new(0, 6, 0, 6) }; error.SetResourceReference(TextBlock.ForegroundProperty, "RedBrush"); panel.Children.Add(error);
        var apply = new Wpf.Ui.Controls.Button { Content = "应用", HorizontalAlignment = HorizontalAlignment.Right, Appearance = Wpf.Ui.Controls.ControlAppearance.Primary }; panel.Children.Add(apply);
        var border = new Border { Child = panel, BorderThickness = new(1), CornerRadius = new(8) };
        border.SetResourceReference(Border.BackgroundProperty, "PaperBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        // Keep the button in the dismissal boundary so a second click closes instead of reopening.
        _datePopup = new Popup { Child = border, PlacementTarget = CalendarButton, Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true };
        var popup = _datePopup;
        apply.Click += async (_, _) =>
        {
            try { var value = dates.Value; popup.IsOpen = false; await NavigateAsync(() => Model.SelectDatesAsync(value), preserveDraft: true); }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        border.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { popup.IsOpen = false; CalendarButton.Focus(); args.Handled = true; } };
        popup.Opened += (_, _) => dates.View.Focus();
        popup.IsOpen = true;
    }
    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_exportingReport || Model.Repository == null || Model.CurrentBook == null) return;
        _exportingReport = true;
        if (_datePopup != null) _datePopup.IsOpen = false;
        try
        {
            if (!await TryLeaveEditsAsync()) return;
            var options = await ReportDialog.ShowAsync(this);
            if (options == null) return;
            var ext = options.Format switch { ReportFormat.Docx => "docx", ReportFormat.Pdf => "pdf", _ => "md" };
            var dialog = new SaveFileDialog { Title = "导出任务报告", Filter = $"{ext.ToUpperInvariant()} 文件 (*.{ext})|*.{ext}", DefaultExt = "." + ext, AddExtension = true, FileName = $"{Model.CurrentBook.Name}-任务报告-{DateTime.Now:yyyyMMdd-HHmmss}.{ext}" };
            if (dialog.ShowDialog(this) != true) return;
            // Reports must never overwrite a live book, setting, or backup, even with a custom extension.
            var target = Path.GetFullPath(dialog.FileName);
            var dataRoot = Path.GetFullPath(Model.Library.DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (target.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请将报告保存到 Data 目录之外。");
            Model.IsBusy = true;
            while (_pendingMutations > 0) await Task.Delay(20);
            var snapshot = await Model.Repository.ReadReportAsync(options);
            var report = await Task.Run(() => TaskReport.Create(snapshot, options, DateTimeOffset.Now));
            await ReportWriter.WriteAsync(report, options.Format, target);
            Model.Message = "报告已导出到 " + target;
            try { ExplorerService.Reveal(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { Model.Message = "报告已导出，但无法打开所在目录：" + ex.Message; }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { Model.IsBusy = false; _exportingReport = false; }
    }
}
