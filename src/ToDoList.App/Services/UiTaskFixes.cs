using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToDoList.App.Controls;
using ToDoList.Core;

namespace ToDoList.App.Services;

internal static class UiTaskFixes
{
    public static async Task RunAsync(MainWindow window)
    {
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        var checks = new List<string>(); var failures = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        object? Invoke(object instance, string method, params object?[] args) => instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
        async Task Call(string method, params object?[] args) => await ((Task)Invoke(window, method, args)!).WaitAsync(TimeSpan.FromSeconds(8));
        void Capture(string name)
        {
            window.UpdateLayout(); var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var stream = File.Create(Path.Combine(output, name + ".png")); png.Save(stream);
        }
        try
        {
            var model = window.Model;
            // A dedicated empty data directory prevents touching a running user's notebook.
            if (model.HasBook) throw new InvalidOperationException("UI task checks require an empty test Data directory.");
            var book = await model.Library.CreateAsync("TaskFixes", "逐项修复验收");
            await model.RefreshBooksAsync(); await model.OpenBookAsync(book);
            var a = await model.Repository!.AddProjectAsync("模块 A"); var b = await model.Repository.AddProjectAsync("模块 B");
            await model.RefreshProjectsAsync(); Invoke(window, "SyncSelectors", false);
            model.Settings.Mode = "深色"; ThemeService.Apply(model.Settings); await Settle();
            var editor = (RichEditor)window.FindName("DraftEditor"); var input = (RichTextBox)editor.FindName("Editor");
            input.Focus(); input.Selection.Text = "测试";
            input.SelectAll(); var data = editor.CreateClipboardData(); input.CaretPosition = input.Document.ContentEnd;
            var paste = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText);
            Invoke(editor, "OnPaste", input, paste);
            Check(editor.GetContent().PlainText == "测试测试", "Same-editor copy/paste preserves text");
            Check(editor.GetContent().Paragraphs.SelectMany(p => p.Runs).All(r => r.Color == null), "Same-editor paste uses automatic color, never serialized black");
            Check(input.Document.Foreground is SolidColorBrush ink && ink.Color == ((SolidColorBrush)window.FindResource("InkBrush")).Color, "Document inherits dark theme ink");
            var before = editor.GetContent().PlainText;
            editor.PastePlainText("\n第二行\n\n第三行 👩‍💻");
            Check(editor.GetContent().PlainText == before + "\n第二行\n\n第三行 👩‍💻", "Multiline, empty lines and emoji paste without crash");
            input.Undo(); input.Redo();
            Check(editor.GetContent().PlainText.Contains("第三行"), "Paste undo/redo preserves multiline content");
            editor.SetContent(new RichContent(1, [new RichParagraph([new RichRun("有格式", true, true, true, "#FF0000")])]));
            input.SelectAll(); data = editor.CreateClipboardData();
            Invoke(editor, "OnPaste", input, new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText));
            Check(editor.GetContent().PlainText == "有格式" && editor.GetContent().Paragraphs.SelectMany(p => p.Runs).All(r => !r.Bold && !r.Italic && !r.Underline && r.Color == null), "Plain paste replaces formatted selection without source styling");
            var savedClipboard = Clipboard.GetDataObject();
            try
            {
                const string multiline = "测试\n第二行\n\n表情 👩‍💻";
                editor.SetContent(RichContent.FromText(multiline)); input.Focus();
                for (var iteration = 0; iteration < 100; iteration++)
                {
                    input.SelectAll(); ApplicationCommands.Copy.Execute(null, input);
                    input.CaretPosition = input.Document.ContentEnd; ApplicationCommands.Paste.Execute(null, input);
                    if (editor.GetContent().PlainText != multiline + multiline) throw new InvalidOperationException("Clipboard append mismatch at " + iteration);
                    input.Undo(); input.Redo(); input.SelectAll(); ApplicationCommands.Paste.Execute(null, input);
                    if (editor.GetContent().PlainText != multiline) throw new InvalidOperationException("Clipboard replacement mismatch at " + iteration);
                }
                Check(true, "100 repeated WPF clipboard command cycles: copy, append, undo, redo, replace");
            }
            finally { if (savedClipboard != null) Clipboard.SetDataObject(savedClipboard, true); }
            editor.SetContent(RichContent.FromText("草稿内容")); input.CaretPosition = input.Document.ContentEnd;
            var draft = editor.GetContent().ToJson();
            var selector = (ComboBox)window.FindName("DraftProject"); selector.SelectedItem = model.DraftProjects.First(p => p.Id == a.Id);
            await Call("NavigateAsync", (Func<Task>)(() => model.SelectFilterAsync(TaskFilter.Open)), false, true);
            Check(editor.GetContent().ToJson() == draft && (selector.SelectedItem as ProjectInfo)?.Id == a.Id, "Tab navigation preserves draft and project without prompting");
            ((ListBox)window.FindName("ProjectList")).SelectedItem = model.Projects.First(p => p.Id == b.Id);
            await WaitUntil(() => model.CurrentProjectId == b.Id && (selector.SelectedItem as ProjectInfo)?.Id == b.Id);
            Check(editor.GetContent().ToJson() == draft, "Module navigation preserves draft text");
            ((ListBox)window.FindName("ProjectList")).SelectedItem = model.Projects.First(p => p.Id == null);
            await WaitUntil(() => model.CurrentProjectId == null);
            Check((selector.SelectedItem as ProjectInfo)?.Id == b.Id, "All modules preserves draft assignment");
            var tabs = (TabControl)window.FindName("Tabs"); tabs.SelectedItem = tabs.Items.Cast<TabItem>().First(t => (string)t.Tag == "All");
            await WaitUntil(() => model.Filter == TaskFilter.All);
            await Call("AddDraftAsync");
            var saved = (await model.Repository.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items.Single();
            Check(saved.PlainText == "草稿内容" && saved.ProjectId == b.Id, "Submission persists final draft assignment");
            Check((await new Storage.TaskRepository(model.CurrentBook!.Path).QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items.Single().PlainText == saved.PlainText, "Committed task visible from independent connection before exit");
            await window.MoveProjectToAsync(b, a, false);
            Check(model.Projects.Where(p => p.Id != null).Select(p => p.Id).SequenceEqual(new[] { b.Id, a.Id }), "Reorder updates sidebar order");
            Check(model.DraftProjects.Where(p => p.Id != null && p.Id != "__new").Select(p => p.Id).SequenceEqual(new[] { b.Id, a.Id }), "Reorder updates draft dropdown order");
            Check((await model.Repository.GetProjectsAsync())[0].Id == b.Id, "Reorder persisted immediately");
            Check((selector.SelectedItem as ProjectInfo)?.Id == b.Id, "Reorder retains draft assignment");
            await Settle();
            var card = window.FindCard(model.Tasks.Single())!;
            Invoke(window, "Task_EditRequested", card, EventArgs.Empty); await Settle();
            card.ActiveEditor!.PastePlainText("修改");
            var navigation = Call("NavigateAsync", (Func<Task>)(() => model.SelectFilterAsync(TaskFilter.Open)), false, true);
            await Settle();
            var editDialog = MainWindow.Descendants<Wpf.Ui.Controls.ContentDialog>(window).FirstOrDefault(d => d.IsVisible);
            Check(editDialog != null && !navigation.IsCompleted, "Existing-task edits still require save confirmation");
            editDialog!.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Close); await navigation;
            Check(model.Filter == TaskFilter.All && card.ActiveEditor != null, "Cancel navigation retains existing edit");
            Invoke(window, "EndRowEdit");
            await Settle(); Capture("dark-taskfixes");
            foreach (var scope in new[] { DateScope.Day, DateScope.Range })
            {
                var form = new DateSelectionForm(false, new(scope, DateTime.Today, DateTime.Today));
                form.View.Measure(new Size(420, double.PositiveInfinity)); form.View.Arrange(new Rect(0, 0, 420, form.View.DesiredSize.Height)); form.View.UpdateLayout();
                var grid = (Grid)form.View; var pickerGrid = (Grid)grid.Children[scope == DateScope.Day ? 1 : 2];
                Check(Math.Abs(pickerGrid.ActualWidth - ((FrameworkElement)grid.Children[0]).ActualWidth) < 1, "Calendar aligned width " + scope);
            }
            var reportTask = ReportDialog.ShowAsync(window); await Settle();
            var reportDialog = MainWindow.Descendants<Wpf.Ui.Controls.ContentDialog>(window).First(d => d.IsVisible);
            var reportScope = MainWindow.Descendants<ComboBox>(reportDialog).First(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "DateScope");
            foreach (var scope in new[] { DateScope.Year, DateScope.Month, DateScope.Day, DateScope.Range })
            {
                reportScope.SelectedValue = scope; await Settle();
                var pickers = MainWindow.Descendants<Wpf.Ui.Controls.CalendarDatePicker>(reportDialog).Where(p => p.IsVisible).ToArray();
                foreach (var picker in pickers)
                {
                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(picker);
                    ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                    await Settle();
                    var calendar = UiInteractionChecks.PopupElements<Calendar>().First(c => c.IsVisible);
                    var anchor = picker.PointToScreen(new Point()); var edge = calendar.PointToScreen(new Point());
                    Check(Math.Abs(anchor.X - edge.X) <= 1 && Math.Abs(picker.ActualWidth - calendar.ActualWidth) <= 1, "Report expanded calendar aligns " + scope + "/" + System.Windows.Automation.AutomationProperties.GetAutomationId(picker));
                    Check(Math.Abs(picker.ActualWidth - reportScope.ActualWidth) <= 1, "Report date field aligns with upper selector " + scope);
                    picker.IsCalendarOpen = false;
                }
            }
            Capture("report-range-aligned");
            reportDialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Close); await reportTask;
            var deleted = await model.Repository.DeleteTaskAsync(saved.Id, saved.Revision);
            await model.SelectFilterAsync(TaskFilter.Deleted); Invoke(window, "SyncSelectors", false); await Settle();
            var row = model.Tasks.Single(); window.StartDeletedSelection(row);
            Check(row.SelectionMode && row.SelectedForDeletion, "Deleted task multi-selection activated");
            await model.ReloadAsync(true);
            Check(model.Tasks.Single().SelectedForDeletion, "Selection retained by ID after row recreation");
            Capture("deleted-selection");
            var deleteRequest = new Dictionary<string, long> { [deleted.Id] = deleted.Revision };
            var deleting = window.DeletePermanentlyAsync(deleteRequest); await Settle();
            var deleteDialog = MainWindow.Descendants<Wpf.Ui.Controls.ContentDialog>(window).First(d => d.IsVisible);
            deleteDialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Close); await deleting;
            Check(model.Tasks.Count == 1 && model.Tasks[0].SelectedForDeletion, "Cancel permanent delete preserves data and selection");
            deleting = window.DeletePermanentlyAsync(deleteRequest); await Settle();
            deleteDialog = MainWindow.Descendants<Wpf.Ui.Controls.ContentDialog>(window).First(d => d.IsVisible);
            deleteDialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Primary); await deleting;
            Check(model.Tasks.Count == 0, "Permanent delete removes selected task");
            model.Settings.Mode = "浅色"; ThemeService.Apply(model.Settings); editor.SetContent(RichContent.FromText("测试")); input.CaretPosition = input.Document.ContentEnd; editor.PastePlainText("测试\n第二行");
            Check(editor.GetContent().Paragraphs.SelectMany(p => p.Runs).All(r => r.Color == null), "Light theme paste remains automatic");
            Capture("light-taskfixes");
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            File.WriteAllText(Path.Combine(output, "taskfixes.json"), JsonSerializer.Serialize(new { Passed = failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown();
        }
    }
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(200); }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        do { await Settle(); if (condition()) return; } while (DateTime.UtcNow < until);
        throw new TimeoutException("UI navigation did not complete (possibly prompted for draft).");
    }
}
