using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using ToDoList.App.Controls;
using ToDoList.Core;

namespace ToDoList.App.Services;

internal static class UiFeatures
{
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    private static void Enter(RichEditor editor, bool shift = false)
    {
        var before = new byte[256]; GetKeyboardState(before); var pressed = (byte[])before.Clone();
        pressed[0x10] = pressed[0xA0] = shift ? (byte)0x80 : (byte)0;
        try
        {
            SetKeyboardState(pressed);
            var input = (RichTextBox)editor.FindName("Editor");
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        finally { SetKeyboardState(before); }
    }
    public static async Task RunAsync(MainWindow window)
    {
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        var checks = new List<string>(); var failures = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        void Click(string name) => ((ButtonBase)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        void Capture(string name)
        {
            window.UpdateLayout(); var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var stream = File.Create(Path.Combine(output, name + ".png")); png.Save(stream);
        }
        try
        {
            var model = window.Model;
            if (!model.HasBook) await model.OpenBookAsync(await model.Library.CreateAsync("Features", "筛选与报告回归"));
            var repo = model.Repository!;
            var task = await repo.AddTaskAsync(RichContent.FromText("中文 👩‍💻 👍🏽 & <xml> **原文**\n第二行"), null, "测试模块");
            var project = task.ProjectId;
            await repo.AddTaskAsync(RichContent.FromText(new string('长', 3000)), project);
            var completed = await repo.AddTaskAsync(RichContent.FromText("已完成任务"), null);
            completed = await repo.SetStatusAsync(completed.Id, TodoStatus.Completed, completed.Revision);
            await repo.DeleteTaskAsync(completed.Id, completed.Revision);
            await model.RefreshBooksAsync(); await model.RefreshProjectsAsync(); await model.SelectFilterAsync(TaskFilter.All); await Settle();
            typeof(MainWindow).GetMethod("SyncSelectors", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [false]);
            Check(model.Tasks.Count == 3 && model.Tasks.Any(t => t.IsDeleted), "All includes deleted tasks");
            Check(((TabItem)((TabControl)window.FindName("Tabs")).Items[0]).Header.Equals("所有"), "All is first tab");
            foreach (var scope in new[] { DateScope.Year, DateScope.Month, DateScope.Day })
            {
                await model.SelectDatesAsync(new(scope, DateTime.Today, DateTime.Today));
                Check(model.Tasks.Count == 3 && model.IsCalendarFilter, "Calendar " + scope);
            }
            await model.SelectProjectAsync(project); Check(model.Tasks.Count == 2 && model.IsCalendarFilter, "Module switch retains calendar");
            await model.SelectProjectAsync(null); await model.SelectFilterAsync(TaskFilter.All);
            var settings = (FrameworkElement)window.FindName("SettingsPanel");
            Click("SettingsButton"); await Settle(); Check(settings.Visibility == Visibility.Visible, "Settings opens");
            Click("SettingsButton"); await Settle(); Check(settings.Visibility == Visibility.Collapsed, "Settings toggles closed");
            Click("SettingsButton"); await Settle();
            ((UIElement)window.FindName("TaskContent")).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            await Settle(); Check(settings.Visibility == Visibility.Collapsed, "Outside click closes settings");
            Click("CalendarButton"); await Settle();
            var icon = (Wpf.Ui.Controls.SymbolIcon)((Button)window.FindName("CalendarButton")).Content;
            checks.Add($"Calendar icon: {icon.Symbol}, {(int)icon.Symbol:X}, {icon.FontFamily.Source}, {icon.FontStyle}");
            var popupField = typeof(MainWindow).GetField("_datePopup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Check(((Popup)popupField.GetValue(window)!).IsOpen, "Calendar popup opens");
            var popupVisual = (FrameworkElement)((Popup)popupField.GetValue(window)!).Child; popupVisual.UpdateLayout();
            var popupBitmap = new RenderTargetBitmap((int)popupVisual.ActualWidth, (int)popupVisual.ActualHeight, 96, 96, PixelFormats.Pbgra32); popupBitmap.Render(popupVisual);
            var popupEncoder = new PngBitmapEncoder(); popupEncoder.Frames.Add(BitmapFrame.Create(popupBitmap)); using (var popupFile = File.Create(Path.Combine(output, "calendar.png"))) popupEncoder.Save(popupFile);
            Click("CalendarButton"); Check(!((Popup)popupField.GetValue(window)!).IsOpen, "Calendar popup toggles closed");
            Click("CalendarButton"); await Settle();
            var datePopup = (Popup)popupField.GetValue(window)!;
            var calendarScope = MainWindow.Descendants<ComboBox>(datePopup.Child).Single(c => AutomationProperties.GetAutomationId(c) == "DateScope");
            calendarScope.SelectedIndex = 0;
            MainWindow.Descendants<Wpf.Ui.Controls.CalendarDatePicker>(datePopup.Child).Single(p => AutomationProperties.GetAutomationId(p) == "DateSelection").Date = DateTime.Today.AddYears(-1);
            MainWindow.Descendants<Button>(datePopup.Child).Single(b => Equals(b.Content, "应用")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Settle();
            Check(!datePopup.IsOpen && model.IsCalendarFilter && model.Tasks.Count == 0 && ((TabControl)window.FindName("Tabs")).SelectedIndex == -1, "Calendar apply navigates and clears ordinary tab selection");
            ((TabControl)window.FindName("Tabs")).SelectedIndex = 0; await Settle();
            Check(!model.IsCalendarFilter && model.Tasks.Count == 3, "Ordinary tab exits calendar filter");
            Click("CalendarButton"); await Settle();
            ((UIElement)window.FindName("TaskContent")).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            Check(!((Popup)popupField.GetValue(window)!).IsOpen, "Outside click closes calendar");
            Click("CalendarButton"); await Settle();
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!((Popup)popupField.GetValue(window)!).IsOpen, "Escape closes calendar");
            var editor = (RichEditor)window.FindName("DraftEditor");
            foreach (var size in new[] { 12d, 14d, 16d, 18d })
            {
                model.Settings.FontSize = size; ThemeService.Apply(model.Settings); editor.SetContent(RichContent.FromText("")); editor.FocusEditor(); await Settle();
                var box = (RichTextBox)editor.FindName("Editor"); var hint = (TextBlock)editor.FindName("Placeholder");
                var rect = box.Document.ContentStart.GetInsertionPosition(System.Windows.Documents.LogicalDirection.Forward)!.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward);
                var point = box.TranslatePoint(rect.TopLeft, (UIElement)hint.Parent);
                Check(Math.Abs(Canvas.GetLeft(hint) - point.X) < 1 && Math.Abs(Canvas.GetTop(hint) - point.Y) < 1 && hint.FontSize == size, "Placeholder aligned at " + size);
                Capture("editor-" + size);
                editor.SetContent(RichContent.FromText(" ")); Check(hint.Visibility == Visibility.Collapsed, "Whitespace hides hint " + size);
            }
            model.Settings.FontSize = 14; ThemeService.Apply(model.Settings);
            editor.SetContent(RichContent.FromText("第一行")); editor.FocusEditor(); await Settle();
            Enter(editor, shift: true); await Settle();
            Check(editor.GetContent().PlainText == "第一行\n", "Shift Enter inserts newline without submission");
            var composing = typeof(RichEditor).GetField("_composing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            composing.SetValue(editor, true); Enter(editor); composing.SetValue(editor, false);
            Check(editor.GetContent().PlainText == "第一行\n", "IME composition Enter does not submit");
            editor.SetContent(RichContent.FromText("Enter 提交回归")); editor.FocusEditor(); await Settle();
            Enter(editor);
            await Settle(); Check(editor.GetContent().PlainText.Length == 0 && model.Tasks.Any(t => t.Item.PlainText == "Enter 提交回归"), "Enter adds task");
            var editRow = model.Tasks.Single(t => t.Item.PlainText == "Enter 提交回归");
            var list = (ListBox)window.FindName("TaskList"); list.ScrollIntoView(editRow); await Settle();
            var card = window.FindCard(editRow)!;
            typeof(MainWindow).GetMethod("Task_EditRequested", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [card, EventArgs.Empty]);
            await Settle(); var inline = card.ActiveEditor!;
            inline.SetContent(RichContent.FromText("")); inline.FocusEditor(); await Settle();
            var inlineHint = (TextBlock)inline.FindName("Placeholder");
            Check(inlineHint.IsVisible && inlineHint.Text.Contains("保存") && !double.IsNaN(Canvas.GetLeft(inlineHint)), "Inline editor has aligned save hint"); Capture("inline-editor");
            card.ActiveProjectSelector!.SelectedValue = project;
            inline.SetContent(RichContent.FromText("Enter 保存回归")); inline.FocusEditor(); Enter(inline); await Settle();
            Check(!editRow.IsEditing && editRow.Item.PlainText == "Enter 保存回归" && editRow.Item.ProjectId == project, "Enter saves existing task content and module");
            var dialogTask = ReportDialog.ShowAsync(window); await Settle(); Capture("report-dialog");
            var activeDialog = MainWindow.Descendants<Wpf.Ui.Controls.ContentDialog>(window).Single();
            activeDialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Primary);
            var dialogOptions = await dialogTask.WaitAsync(TimeSpan.FromSeconds(3));
            Check(dialogOptions?.Format == ReportFormat.Markdown && dialogOptions.Status == TaskFilter.All && dialogOptions.Dates.Scope == DateScope.Any, "Report dialog defaults and confirmation");
            dialogTask = ReportDialog.ShowAsync(window); await Settle();
            activeDialog = MainWindow.Descendants<Wpf.Ui.Controls.ContentDialog>(window).Single();
            var reportScope = MainWindow.Descendants<ComboBox>(activeDialog).Single(c => AutomationProperties.GetAutomationId(c) == "DateScope");
            reportScope.SelectedIndex = 4;
            var reportFrom = MainWindow.Descendants<Wpf.Ui.Controls.CalendarDatePicker>(activeDialog).Single(p => AutomationProperties.GetAutomationId(p) == "DateFrom");
            var reportUntil = MainWindow.Descendants<Wpf.Ui.Controls.CalendarDatePicker>(activeDialog).Single(p => AutomationProperties.GetAutomationId(p) == "DateUntil");
            reportFrom.Date = DateTime.Today; reportUntil.Date = DateTime.Today.AddDays(-1);
            activeDialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Primary); await Settle();
            Check(!dialogTask.IsCompleted, "Report rejects reversed date range without closing");
            reportFrom.Date = DateTime.Today.AddDays(-2); reportUntil.Date = DateTime.Today;
            var selectors = MainWindow.Descendants<ComboBox>(activeDialog).ToList();
            selectors.Single(c => c.DisplayMemberPath == "Name").SelectedValue = project;
            selectors.Single(c => c.Items.Contains("已完成")).SelectedIndex = 2;
            selectors.Single(c => c.Items.Contains("Word 文档 (.docx)")).SelectedIndex = 1;
            activeDialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Primary);
            var custom = await dialogTask.WaitAsync(TimeSpan.FromSeconds(3));
            Check(custom != null && custom.ProjectId == project && custom.Status == TaskFilter.Completed && custom.Format == ReportFormat.Docx && custom.Dates.End.Date == DateTime.Today, "Report module, status, date and format are independent");
            Check(model.Filter == TaskFilter.All && model.CurrentProjectId == null, "Report configuration preserves main view");
            var options = new ReportOptions(null, TaskFilter.All, DateSelection.Any, ReportFormat.Markdown);
            var snapshot = await repo.ReadReportAsync(options); var report = TaskReport.Create(snapshot, options, DateTimeOffset.Now);
            foreach (var format in Enum.GetValues<ReportFormat>())
            {
                var ext = format == ReportFormat.Markdown ? "md" : format.ToString().ToLowerInvariant();
                var path = Path.Combine(output, "report." + ext); await ReportWriter.WriteAsync(report, format, path);
                Check(new FileInfo(path).Length > 100, "Export " + format);
            }
            using (var zip = ZipFile.OpenRead(Path.Combine(output, "report.docx")))
            using (var stream = zip.GetEntry("word/document.xml")!.Open())
            {
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                Check(XDocument.Load(stream).Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))).SequenceEqual(report.Lines.Select(l => l.Text)), "DOCX has every report line exactly");
            }
            Check(File.ReadAllText(Path.Combine(output, "report.md")) == report.ToMarkdown(), "Markdown content matches report");
            var blocked = Path.Combine(output, "blocked.md"); Directory.CreateDirectory(blocked);
            try { await ReportWriter.WriteAsync(report, ReportFormat.Markdown, blocked); throw new InvalidOperationException("Expected export failure"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Check(!Directory.GetFiles(output, "blocked.md.*.tmp").Any(), "Failed export cleans temporary file"); }
            window.Width = 800; window.Height = 560; await Settle(); Check(editor.ActualHeight == 76, "Compact input height"); Capture("compact");
            window.Width = 1100; window.Height = 760; await Settle(); Capture("features");
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            File.WriteAllText(Path.Combine(output, "features.json"), JsonSerializer.Serialize(new { Passed = failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown();
        }
    }
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(250); }
}
