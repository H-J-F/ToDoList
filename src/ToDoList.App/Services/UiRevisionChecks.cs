using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using ToDoList.App.Controls;
using ToDoList.Core;
using ToDoList.Storage;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace ToDoList.App.Services;

// Opt-in WPF checks. Always launched with an isolated, empty --data-dir.
internal static partial class UiRevisionChecks
{
    public static async Task RunAsync(MainWindow window)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--edit-scroll")) { await RunEditScrollAsync(window); return; }
        if (args.Contains("--color-paste")) { await RunColorPasteAsync(window); return; }
        if (args.Contains("--selection-recycling")) { await RunSelectionRecyclingAsync(window); return; }
        if (args.Contains("--exit-draft-save") || args.Contains("--exit-draft-discard"))
        { await RunDraftExitAsync(window, args.Contains("--exit-draft-save")); return; }
        var checks = new List<string>(); var failures = new List<string>();
        var samples = new List<object>();
        var completed = false;
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        void Check(bool condition, string name) { if (condition) checks.Add(name); else failures.Add(name); Save(); }
        void Save() => File.WriteAllText(Path.Combine(output, "revision.json"), JsonSerializer.Serialize(new { Completed = completed, Passed = failures.Count == 0, Checks = checks, Failures = failures, AnimationSamples = samples }, new JsonSerializerOptions { WriteIndented = true }));
        void Capture(string name)
        {
            window.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
        }
        var closing = false;
        try
        {
            var model = window.Model;
            if (model.HasBook) throw new InvalidOperationException("Revision checks require an empty test data directory.");
            var book = await model.Library.CreateAsync("Revision", "任务修正验收");
            await model.RefreshBooksAsync(); await model.OpenBookAsync(book);
            var repo = model.Repository!;
            var shortTask = await repo.AddTaskAsync(RichContent.FromText("短文本任务"), null);
            var longTask = await repo.AddTaskAsync(RichContent.FromText(string.Join("\n", Enumerable.Range(1, 8).Select(n => $"第 {n} 行，多行任务与自动换行的编辑测量检查 👩‍💻。"))), null);
            var unedited = await repo.AddTaskAsync(RichContent.FromText("尚未编辑"), null);
            await model.SelectFilterAsync(TaskFilter.All); Invoke(window, "SyncSelectors", false); await Settle();
            model.Settings.ReduceMotion = false;
            foreach (var width in new[] { 850d, 1150d })
            foreach (var font in new[] { 12d, 18d })
            foreach (var id in new[] { shortTask.Id, longTask.Id, shortTask.Id })
            {
                window.Width = width; model.Settings.FontSize = font; ThemeService.Apply(model.Settings); await Settle();
                var card = Card(window, id);
                var body = (TextBlock)card.FindName("BodyText"); var start = body.ActualHeight;
                var heights = new List<double>();
                Invoke(window, "Task_EditRequested", card, EventArgs.Empty);
                var host = (ContentControl)card.FindName("EditorHost");
                EventHandler sample = (_, _) => heights.Add(host.ActualHeight);
                CompositionTarget.Rendering += sample;
                try { await Task.Delay(450); window.UpdateLayout(); }
                finally { CompositionTarget.Rendering -= sample; }
                var final = host.ActualHeight;
                Check(heights.Count >= 2 && heights.All(h => h <= Math.Max(start, final) + 1), $"No edit height overshoot: {width}/{font}/{id}/{samples.Count}");
                Check(double.IsNaN(host.Height) && card.ActiveEditor != null, "Editor returns to automatic height");
                samples.Add(new { Width = width, Font = font, Id = id, Start = start, Final = final, Heights = heights });
                Invoke(window, "EndRowEdit"); await Settle();
            }
            var editCard = Card(window, shortTask.Id);
            Invoke(window, "Task_EditRequested", editCard, EventArgs.Empty); await Settle();
            await (Task)Invoke(window, "SaveRowEditAsync")!;
            Check(model.Tasks.Single(t => t.Id == shortTask.Id).Item.EditedAt == null, "Opening and saving without changes does not record edit time");
            Invoke(window, "Task_EditRequested", editCard, EventArgs.Empty); await Settle();
            editCard.ActiveEditor!.SetContent(RichContent.FromText("已修改内容"));
            await (Task)Invoke(window, "SaveRowEditAsync")!; await Settle();
            var editedRow = model.Tasks.Single(t => t.Id == shortTask.Id);
            Check(editedRow.HasEditTime && editedRow.EditLabel.StartsWith("上次修改于") && editedRow.TimeDetails.Contains("上次修改："), "Saved edit appears in card and time tooltip");
            Check(!model.Tasks.Single(t => t.Id == unedited.Id).HasEditTime, "Never-edited task hides edit timestamp");
            model.Settings.ReduceMotion = true; ThemeService.Apply(model.Settings);
            Invoke(window, "Task_EditRequested", editCard, EventArgs.Empty); await Settle();
            Check(double.IsNaN(((ContentControl)editCard.FindName("EditorHost")).Height), "Reduced motion editor has natural height");
            Invoke(window, "EndRowEdit");
            model.Settings.ReduceMotion = false; ThemeService.Apply(model.Settings);
            Invoke(window, "Task_EditRequested", editCard, EventArgs.Empty); Invoke(window, "EndRowEdit"); await Settle();
            Check(editCard.ActiveEditor == null && ((ContentControl)editCard.FindName("EditorHost")).Visibility == Visibility.Collapsed, "Immediate cancellation invalidates queued edit animation");

            foreach (var mode in new[] { "浅色", "深色" })
            {
                model.Settings.Mode = mode; ThemeService.Apply(model.Settings); window.SyncSettings(); await Settle();
                var names = new[] { "Open", "Verification", "Completed" };
                var colors = new[] { "#00CAE5", "#E6A409", "#07E355" };
                for (var i = 0; i < names.Length; i++)
                {
                    var box = (TextBox)window.FindName(names[i] + "ColorSetting");
                    var preview = (Border)window.FindName(names[i] + "ColorPreview");
                    box.Text = colors[i];
                    Check(((SolidColorBrush)preview.Background).Color == (Color)ColorConverter.ConvertFromString(colors[i]), "Color swatch matches input " + mode + names[i]);
                    box.Text = "#123456";
                    Check(((SolidColorBrush)preview.Background).Color == (Color)ColorConverter.ConvertFromString("#123456"), "Color preview responds before save " + mode + names[i]);
                    box.Text = "#12";
                    Check(((SolidColorBrush)preview.Background).Color == (Color)ColorConverter.ConvertFromString("#123456"), "Incomplete input retains last valid preview " + mode + names[i]);
                    await model.SetStatusColorAsync(names[i], "#123456");
                    Check(((SolidColorBrush)window.FindResource("Task" + names[i] + "Brush")).Color == (Color)ColorConverter.ConvertFromString(colors[i]), "Report settings cannot change task palette " + mode + names[i]);
                }
                foreach (var status in new[] { TodoStatus.Open, TodoStatus.Verification, TodoStatus.Completed })
                {
                    var row = model.Tasks.Single(t => t.Id == shortTask.Id);
                    row.Item = await repo.SetStatusAsync(row.Id, status, row.Item.Revision); await Settle();
                    var check = (CheckBox)editCard.FindName("CheckButton");
                    if (status == TodoStatus.Open && mode == "浅色") File.WriteAllText(Path.Combine(output, "checkbox-template.xaml"), System.Windows.Markup.XamlWriter.Save(check.Template));
                    var expected = (Color)ColorConverter.ConvertFromString(colors[(int)status]);
                    Check(((SolidColorBrush)check.Resources["CheckBoxCheckBackgroundStrokeUnchecked"]).Color == expected && ((SolidColorBrush)check.Resources["CheckBoxCheckBackgroundFillChecked"]).Color == expected, "Checkbox state uses independent palette " + mode + status);
                    var stroke = (Border)check.Template.FindName("StrokeBorder", check);
                    var fill = (Border)check.Template.FindName("ControlBorderIconPresenter", check);
                    Check(stroke.BorderBrush is SolidColorBrush borderBrush && borderBrush.Color == expected && (status == TodoStatus.Open || fill.Background is SolidColorBrush fillBrush && fillBrush.Color == expected), "Rendered checkbox border and fill use state color " + mode + status);
                }
            }
            window.SyncSettings(); Invoke(window, "Settings_Click", window, new RoutedEventArgs()); await Settle(); Capture("colors-and-edit-time");
            Invoke(window, "CloseSettings_Click", window, new RoutedEventArgs()); await Settle();
            var otherBook = await model.Library.CreateAsync("Other", "独立配色");
            await model.OpenBookAsync(otherBook); ThemeService.Apply(model.Settings);
            Check(((SolidColorBrush)window.FindResource("TaskOpenBrush")).Color == (Color)ColorConverter.ConvertFromString("#00CAE5"), "Switching books does not change task palette");
            await model.OpenBookAsync(book); await model.SelectFilterAsync(TaskFilter.All); Invoke(window, "SyncSelectors", false); await Settle();
            Check(model.ReportOpenColor == "#123456", "Report palette persists in its own book");

            var options = new ReportOptions(null, TaskFilter.All, DateSelection.Any, ReportFormat.Markdown);
            var report = TaskReport.Create(await repo.ReadReportAsync(options), options, DateTimeOffset.Now);
            Check(report.Lines.Count(l => l.Kind == ReportLineKind.Metadata && l.Text.Contains("上次修改时间：")) == 1, "Report contains edit metadata only for edited tasks");
            foreach (var format in Enum.GetValues<ReportFormat>())
            {
                var ext = format == ReportFormat.Markdown ? "md" : format.ToString().ToLowerInvariant();
                var file = Path.Combine(output, "report." + ext); await ReportWriter.WriteAsync(report, format, file);
                Check(new FileInfo(file).Length > 100, "Report exports " + format);
            }
            using (var zip = ZipFile.OpenRead(Path.Combine(output, "report.docx")))
            using (var stream = zip.GetEntry("word/document.xml")!.Open())
            {
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                Check(XDocument.Load(stream).Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))).SequenceEqual(report.Lines.Select(l => l.Text)), "DOCX preserves every report line including edit time");
            }
            Check(File.ReadAllText(Path.Combine(output, "report.md")) == report.ToMarkdown(), "Markdown matches shared report model");

            foreach (var row in model.Tasks.ToArray()) await repo.DeleteTaskAsync(row.Id, row.Item.Revision);
            await model.SelectFilterAsync(TaskFilter.Deleted); Invoke(window, "SyncSelectors", false); await Settle();
            var selected = model.Tasks[0]; var selectedCard = Card(window, selected.Id);
            var trash = (FrameworkElement)selectedCard.FindName("DeletedIcon");
            var point = trash.TranslatePoint(new Point(), selectedCard);
            window.StartDeletedSelection(selected); await Settle();
            var selector = (CheckBox)selectedCard.FindName("DeleteSelectionButton");
            Check(selector.IsVisible && !trash.IsVisible && selector.Content == null && selector.TranslatePoint(new Point(), selectedCard) == point, "Selection replaces trash at the same location without text");
            Invoke(window, "ClearDeletedSelection"); await Settle();
            Check(!selector.IsVisible && trash.IsVisible && !selected.SelectedForDeletion, "Exiting selection restores trash and clears checks");
            window.StartDeletedSelection(selected); await model.ReloadAsync(true); await Settle();
            Check(model.Tasks.Single(r => r.Id == selected.Id).SelectedForDeletion && !((FrameworkElement)Card(window, selected.Id).FindName("DeletedIcon")).IsVisible, "Selection survives recreated task cards");
            Capture("deleted-selection");
            var deletion = window.DeletePermanentlyAsync(new Dictionary<string, long> { [selected.Id] = selected.Item.Revision });
            await Choose(window, ContentDialogButton.Close); await deletion;
            Check(model.Tasks.Count == 3 && model.Tasks.Single(r => r.Id == selected.Id).SelectedForDeletion, "Cancelling deletion keeps selection");
            deletion = window.DeletePermanentlyAsync(new Dictionary<string, long> { [selected.Id] = selected.Item.Revision - 1 });
            await Choose(window, ContentDialogButton.Primary); await deletion;
            await Choose(window, ContentDialogButton.Close);
            Check(model.Tasks.Single(r => r.Id == selected.Id).SelectedForDeletion, "Failed deletion keeps selection");
            deletion = window.DeletePermanentlyAsync(new Dictionary<string, long> { [selected.Id] = selected.Item.Revision });
            await Choose(window, ContentDialogButton.Primary); await deletion; await Settle();
            Check(model.Tasks.Count == 2 && model.Tasks.All(r => !r.SelectionMode && !r.SelectedForDeletion) && ((FrameworkElement)window.FindName("DeletedActions")).Visibility == Visibility.Collapsed, "Successful deletion exits selection for remaining tasks");
            Check(model.Tasks.All(r => ((FrameworkElement)Card(window, r.Id).FindName("DeletedIcon")).IsVisible), "Remaining tasks restore trash icons");

            Save();
            var draft = (RichEditor)window.FindName("DraftEditor");
            draft.SetContent(RichContent.FromText("退出时取消")); window.Close(); await Settle();
            Check(!window.IsVisible, "Window closes to tray with draft");
            window.RequestExit(); await Choose(window, ContentDialogButton.Close); await Settle();
            Check(window.IsVisible && !((App)Application.Current).ExitRequested, "Cancelled exit resets exit flag and reveals pending draft");
            window.Close(); await Settle(); Check(!window.IsVisible, "Close after cancelled exit still hides to tray");
            window.RestoreFromTray();
            draft.SetContent(RichContent.FromText("退出时保存失败"));
            using (var c = Database.Open(book.Path)) Database.Exec(c, "CREATE TRIGGER fail_add BEFORE INSERT ON Tasks BEGIN SELECT RAISE(ABORT,'test save failure'); END;");
            window.Close(); window.RequestExit(); await Choose(window, ContentDialogButton.Primary); await Choose(window, ContentDialogButton.Close); await Settle();
            Check(!((App)Application.Current).ExitRequested && draft.GetContent().PlainText == "退出时保存失败", "Save failure cancels exit and preserves draft");
            using (var c = Database.Open(book.Path)) Database.Exec(c, "DROP TRIGGER fail_add;");
            // Save one pending row then cancel the draft prompt, proving both exit paths are reachable.
            var restored = await repo.RestoreTaskAsync(model.Tasks[0].Id, model.Tasks[0].Item.Revision);
            await model.SelectFilterAsync(TaskFilter.All); Invoke(window, "SyncSelectors", false); await Settle();
            var pendingCard = Card(window, restored.Id); Invoke(window, "Task_EditRequested", pendingCard, EventArgs.Empty); await Settle();
            SetDirtyContent(pendingCard.ActiveEditor!, "退出时保存编辑");
            Check(pendingCard.HasPendingChanges, "Row exit scenario has a real unsaved edit");
            window.Close(); window.RequestExit(); await Choose(window, ContentDialogButton.Primary); await Choose(window, ContentDialogButton.Close); await Settle();
            Check(!((App)Application.Current).ExitRequested && model.Tasks.Single(r => r.Id == restored.Id).Item.PlainText == "退出时保存编辑", "Exit saves row edit before draft cancellation");
            Invoke(window, "Task_EditRequested", Card(window, restored.Id), EventArgs.Empty); await Settle();
            SetDirtyContent(Card(window, restored.Id).ActiveEditor!, "放弃这个修改");
            window.Close(); window.RequestExit(); await Choose(window, ContentDialogButton.Secondary); await Choose(window, ContentDialogButton.Close); await Settle();
            Check(model.Tasks.Single(r => r.Id == restored.Id).Item.PlainText == "退出时保存编辑" && !((App)Application.Current).ExitRequested, "Exit can discard row edit without changing data");
            draft.SetContent(RichContent.FromText(""));
            window.Close(); await Settle();
            var flashes = 0;
            window.IsVisibleChanged += (_, _) => { if (window.IsVisible) flashes++; };
            window.Closed += (_, _) => { completed = true; Check(flashes == 0, "Clean tray exit never restores window"); Save(); };
            model.IsBusy = true; window.RequestExit(); window.RequestExit(); await Settle();
            Check(!window.IsVisible && !Get<bool>(window, "_closingApproved"), "Hidden exit waits for active work without flashing");
            closing = true; model.IsBusy = false;
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally { if (!closing) { completed = true; Save(); Application.Current.Shutdown(); } }
    }

    private static async Task RunDraftExitAsync(MainWindow window, bool saveDraft)
    {
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        var failures = new List<string>(); var checks = new List<string>();
        void Save() => File.WriteAllText(Path.Combine(output, "draft-exit.json"), JsonSerializer.Serialize(new { Passed = failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            if (window.Model.HasBook) throw new InvalidOperationException("Draft exit checks require empty test data.");
            var book = await window.Model.Library.CreateAsync("Draft", "退出草稿");
            await window.Model.RefreshBooksAsync(); await window.Model.OpenBookAsync(book); Invoke(window, "SyncSelectors", false);
            var editor = (RichEditor)window.FindName("DraftEditor"); editor.SetContent(RichContent.FromText("退出前的草稿"));
            window.Close(); await Settle();
            if (window.IsVisible) failures.Add("Window did not hide to tray"); else checks.Add("Draft window hides to tray");
            window.Closed += (_, _) =>
            {
                using var c = Database.Open(book.Path, true);
                var count = Convert.ToInt64(Database.Scalar(c, "SELECT COUNT(*) FROM Tasks WHERE PlainText='退出前的草稿'"));
                if (count == (saveDraft ? 1 : 0)) checks.Add(saveDraft ? "Draft saved once before exit" : "Draft discarded without insertion");
                else failures.Add("Unexpected draft persistence result");
                Save();
            };
            window.RequestExit();
            await Choose(window, saveDraft ? ContentDialogButton.Primary : ContentDialogButton.Secondary);
        }
        catch (Exception ex) { failures.Add(ex.ToString()); Save(); Application.Current.Shutdown(); }
    }

    private static async Task RunSelectionRecyclingAsync(MainWindow window)
    {
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        var checks = new List<string>(); var failures = new List<string>();
        void Check(bool condition, string name) { if (condition) checks.Add(name); else failures.Add(name); }
        try
        {
            var model = window.Model;
            if (model.HasBook) throw new InvalidOperationException("Recycling checks require empty test data.");
            var book = await model.Library.CreateAsync("Recycling", "滚动复用");
            using (var c = Database.Open(book.Path))
            using (var tx = c.BeginTransaction())
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                for (var i = 0; i < 240; i++)
                {
                    var content = RichContent.FromText("滚动任务 " + i);
                    TaskRepository.Insert(c, new(i.ToString("x32"), null, content.ToJson(), content.PlainText, TodoStatus.Deleted, now, now, null, 2, now, TodoStatus.Open));
                }
                tx.Commit();
            }
            await model.RefreshBooksAsync(); await model.OpenBookAsync(book); await model.SelectFilterAsync(TaskFilter.Deleted);
            Invoke(window, "SyncSelectors", false); await Settle();
            var list = (ListBox)window.FindName("TaskList");
            var scroll = MainWindow.Descendants<ScrollViewer>(list).First();
            scroll.ScrollToTop(); await Settle();
            var row = model.Tasks[0]; window.StartDeletedSelection(row); await Settle();
            var initialCards = MainWindow.Descendants<TaskCard>(list).ToArray();
            scroll.ScrollToBottom(); await Settle();
            var bottomCards = MainWindow.Descendants<TaskCard>(list).ToArray();
            Check(bottomCards.Any(initialCards.Contains) && bottomCards.All(c => c.Row?.Id != row.Id), "Scrolling recycles containers to different task rows");
            Check(bottomCards.All(c => ((CheckBox)c.FindName("DeleteSelectionButton")).IsVisible && ((CheckBox)c.FindName("DeleteSelectionButton")).IsChecked == false && !((FrameworkElement)c.FindName("DeletedIcon")).IsVisible), "Recycled rows do not inherit another task's checked state");
            scroll.ScrollToTop(); await Settle();
            Check(((CheckBox)Card(window, row.Id).FindName("DeleteSelectionButton")).IsChecked == true, "Selected task stays checked after scrolling away and back");
            Invoke(window, "ClearDeletedSelection"); scroll.ScrollToBottom(); await Settle();
            Check(MainWindow.Descendants<TaskCard>(list).All(c => !((CheckBox)c.FindName("DeleteSelectionButton")).IsVisible && ((FrameworkElement)c.FindName("DeletedIcon")).IsVisible), "Exiting selection restores trash on recycled rows");
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            File.WriteAllText(Path.Combine(output, "recycling.json"), JsonSerializer.Serialize(new { Passed = failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown();
        }
    }

    private static TaskCard Card(MainWindow window, string id) => MainWindow.Descendants<TaskCard>(window).First(c => c.Row?.Id == id);
    private static void SetDirtyContent(RichEditor editor, string text)
    {
        var input = (RichTextBox)editor.FindName("Editor"); input.SelectAll(); input.Selection.Text = text;
    }
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(220); }
    private static async Task Choose(MainWindow window, ContentDialogButton button)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Settle();
            var dialog = MainWindow.Descendants<ContentDialog>(window).FirstOrDefault(d => d.IsVisible);
            if (dialog == null) continue;
            dialog.TemplateButtonCommand.Execute(button); await Settle(); return;
        }
        throw new TimeoutException("Expected an exit/deletion dialog.");
    }
}
