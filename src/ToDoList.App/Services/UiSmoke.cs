using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToDoList.App.Controls;
using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.App.Services;
internal static class UiSmoke
{
    public static async Task RunAsync(MainWindow window)
    {
        var output = Path.Combine(window.Model.Library.DataDirectory, "..", "screenshots"); Directory.CreateDirectory(output);
        var log = new List<string>();
        try
        {
            var model = window.Model;
            await Settle(); Capture(window, Path.Combine(output, "01-first-launch.png"));
            var createDialog = Dialogs.CreateBook(window); await Settle();
            var dialog = MainWindow.Descendants<Wpf.Ui.Controls.ContentDialog>(window).Single();
            var fields = MainWindow.Descendants<TextBox>(dialog).Where(t => t.MaxLength is 64 or 100).ToList();
            fields.Single(t => t.MaxLength == 64).Text = "Book-1"; fields.Single(t => t.MaxLength == 100).Text = "测试待办书";
            dialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Primary); await Settle();
            Check(!createDialog.IsCompleted, "Invalid book identifier keeps ContentDialog and inputs open", log);
            fields.Single(t => t.MaxLength == 64).Text = "Book2026";
            Capture(window, Path.Combine(output, "dialog-create.png"));
            dialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Primary);
            var createdInput = await createDialog;
            Check(createdInput?.Name == "Book2026", "ContentDialog accepts letters and digits", log);
            Dialogs.ClearBookDraft();
            if (!model.HasBook)
            {
                var book = await model.Library.CreateAsync("Journal", "我的工作与生活");
                var repo = new TaskRepository(book.Path);
                await repo.AddTaskAsync(new(1, [new([new("给新项目画一张草图", true), new("  ✨")])]), null, "设计工作");
                await repo.AddTaskAsync(RichContent.FromText("读完《小王子》的下一章 📚"), null, "学习充电");
                await repo.AddTaskAsync(RichContent.FromText("傍晚散步，给自己一点放空时间 🌱"), null, "好好生活");
                var done = await repo.AddTaskAsync(RichContent.FromText("整理桌面，泡一杯喜欢的茶 ☕"), null);
                await repo.SetStatusAsync(done.Id, TodoStatus.Completed, done.Revision);
                var pending = await repo.AddTaskAsync(RichContent.FromText("确认新版页面的配色与文案"), null);
                await repo.SetStatusAsync(pending.Id, TodoStatus.Verification, pending.Revision);
                await repo.AddTaskAsync(new(1, [new([new("今天的小目标：", true, Color: "#8270AC"), new("做完一件，就给自己一个勾。")]), new([new("不着急，按自己的节奏来。")])]), null);
                await model.RefreshBooksAsync(); await model.OpenBookAsync(book);
                ((ComboBox)window.FindName("BookSelector")).SelectedItem = model.Books.First(b => b.Name == book.Name);
                ((ListBox)window.FindName("ProjectList")).SelectedIndex = 0;
                ((ComboBox)window.FindName("DraftProject")).SelectedIndex = 0;
            }
            await Settle(); Capture(window, Path.Combine(output, "02-journal-light.png"));
            Check(model.Tasks.Count > 0, "Task list loads", log);
            SystemCommands.MaximizeWindow(window); await Settle(); Check(window.WindowState == WindowState.Maximized, "Window maximizes", log);
            SystemCommands.RestoreWindow(window); await Settle(); Check(window.WindowState == WindowState.Normal, "Window restores", log);
            SystemCommands.MinimizeWindow(window); await Settle(); Check(window.WindowState == WindowState.Minimized, "Window minimizes", log);
            SystemCommands.RestoreWindow(window); await Settle();
            var list = (ListBox)window.FindName("TaskList");
            Check(MainWindow.Descendants<TaskCard>(list).Any(), "Real task containers realized", log);
            var checkboxRow = model.Tasks.First(r => !r.IsCompleted && !r.IsVerification);
            var checkboxCard = window.FindCard(checkboxRow)!;
            var checkbox = (CheckBox)checkboxCard.FindName("CheckButton");
            checkbox.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(checkbox), Environment.TickCount, System.Windows.Input.Key.Space) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            await Settle(); Check(checkboxRow.IsCompleted && model.Tasks.Contains(checkboxRow), "Keyboard checkbox completes without disappearing in Today", log);
            checkbox.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(checkbox), Environment.TickCount, System.Windows.Input.Key.Space) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            await Settle(); Check(!checkboxRow.IsCompleted, "Keyboard checkbox restores unfinished state", log);
            checkbox.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            await Task.Delay(750); await Settle();
            Check(checkboxRow.IsVerification, "600ms hold marks verification", log);
            checkbox.ReleaseMouseCapture();
            checkbox.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            await Task.Delay(750); await Settle();
            Check(!checkboxRow.IsVerification && !checkboxRow.IsCompleted, "Second hold cancels verification", log);
            checkbox.ReleaseMouseCapture();
            var row = model.Tasks.First(); var card = window.FindCard(row)!;
            card.BeginEdit(); var original = card.ActiveEditor!.GetContent();
            card.ActiveEditor.SetContent(new(1, [new([new("富文本 👩‍💻❤️", true, true, true, "#BC6852", "https://example.com")])]));
            var roundTrip = card.ActiveEditor.GetContent(); log.Add("Editor roundtrip: " + roundTrip.ToJson()); Check(roundTrip.PlainText == "富文本 👩‍💻❤️" && roundTrip.Paragraphs[0].Runs[0].Bold && roundTrip.Paragraphs[0].Runs[0].Link == "https://example.com", "WPF editor rich-text roundtrip", log);
            card.ActiveEditor.SetContent(original); card.EndEdit(); row.IsEditing = false;
            var completedRow = model.Tasks.First(r => r.IsCompleted); var originalCompleted = completedRow.Item.CompletedAt;
            var completedCard = window.FindCard(completedRow)!;
            var deleteMenu = (MenuItem)completedCard.FindName("DeleteRestoreMenu");
            deleteMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Settle();
            Check(completedRow.IsDeleted && model.Tasks.Contains(completedRow), "Context menu delete retains task in Today", log);
            var body = (TextBlock)completedCard.FindName("BodyText");
            Check(body.Inlines.Cast<System.Windows.Documents.Inline>().Any(i => i.TextDecorations.Any(d => d.Location == TextDecorationLocation.Strikethrough && d.Pen?.Brush is SolidColorBrush b && b.Color == ((SolidColorBrush)window.FindResource("RedBrush")).Color)), "Deleted text has an independent red strikethrough", log);
            completedCard.BeginEdit(); Check(completedCard.ActiveEditor == null, "Deleted tasks cannot enter editing", log);
            Capture(window, Path.Combine(output, "deleted-in-today.png"));
            deleteMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Settle();
            Check(completedRow.IsCompleted && completedRow.Item.CompletedAt == originalCompleted, "Context menu restores original completion timestamp", log);
            var stale = model.Tasks.First(r => !r.IsCompleted && !r.IsDeleted); var staleStatus = stale.Item.Status;
            await model.Repository!.UpdateContentAsync(stale.Id, RichContent.FromText(stale.Item.PlainText + " · 更新"), stale.Item.Revision);
            bool rejected = false;
            try { await model.DeleteRestoreAsync(stale); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && stale.Item.Status == staleStatus && !stale.IsBusy, "Stale deletion rolls UI back and clears busy state", log);
            await model.ReloadAsync(); await Settle();
            await Task.Delay(3200);
            foreach (var palette in new[] { "浅蓝", "青绿", "橙色", "紫色" })
            {
                model.Settings.AccentPreset = palette; ThemeService.Apply(model.Settings); await Settle(); Capture(window, Path.Combine(output, "theme-" + palette + ".png"));
            }
            model.Settings.Mode = "深色"; model.Settings.AccentPreset = "浅蓝"; ThemeService.Apply(model.Settings); window.SyncSettings(); await Settle(); Capture(window, Path.Combine(output, "03-journal-dark.png"));
            ((Border)window.FindName("SettingsPanel")).Visibility = Visibility.Visible; await Settle(); Capture(window, Path.Combine(output, "04-settings-dark.png"));
            ((Border)window.FindName("SettingsPanel")).Visibility = Visibility.Collapsed;
            foreach (var size in new double[] { 12, 14, 16, 18 })
            {
                window.Width = 800; window.Height = 560; model.Settings.FontSize = size; ThemeService.Apply(model.Settings); await Settle();
                var editor = (RichEditor)window.FindName("DraftEditor"); var p = editor.TransformToAncestor(window).Transform(new Point());
                Check(p.Y >= 0 && p.Y + editor.ActualHeight <= window.ActualHeight, $"Input visible at minimum window, font {size}", log);
            }
            Capture(window, Path.Combine(output, "05-minimum-large-font.png"));
            model.Settings.Mode = "浅色"; model.Settings.FontSize = 14; ThemeService.Apply(model.Settings); window.Width = 1100; window.Height = 760;
            var repo2 = model.Repository!;
            var batch = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => repo2.AddTaskAsync(RichContent.FromText($"动画测试 {i} ✅"), null)));
            await model.SelectFilterAsync(TaskFilter.Open); await Settle();
            var before = model.Tasks.Count;
            await Task.WhenAll(model.Tasks.Where(r => batch.Any(t => t.Id == r.Id)).Take(20).Select(r => model.ChangeStatusAsync(r, false)));
            await Settle(); Check(model.Tasks.Count == before - 20, "20 concurrent state changes remove exactly 20 rows", log);
            Check(MainWindow.Descendants<TaskCard>(list).All(c => c.Opacity == 1 && double.IsNaN(c.Height)), "Recycled containers reset animations", log);
            await model.SelectFilterAsync(TaskFilter.Completed); await Settle();
            var undo = model.Tasks.Take(20).ToList(); await Task.WhenAll(undo.Select(r => model.ChangeStatusAsync(r, false)));
            Check(undo.All(r => !model.Tasks.Contains(r)), "Concurrent uncomplete removes completed rows", log);
            await model.SelectFilterAsync(TaskFilter.Open); await Settle();
            var deletedBatch = model.Tasks.Where(r => batch.Any(t => t.Id == r.Id)).Take(20).ToList();
            var deletion = Task.WhenAll(deletedBatch.Select(r => model.DeleteRestoreAsync(r)));
            list.ScrollIntoView(model.Tasks.Last()); window.Width = 1000;
            await model.SelectFilterAsync(TaskFilter.Today); await deletion; await model.ReloadAsync(); await Settle();
            Check(deletedBatch.All(r => model.Tasks.Single(t => t.Id == r.Id).IsDeleted), "20 deletes survive simultaneous resize, scroll and view switch", log);
            var deletedTab = ((TabControl)window.FindName("Tabs")).Items.Cast<TabItem>().Single(t => (string)t.Tag == "Deleted"); deletedTab.IsSelected = true; await Settle();
            Check(model.Filter == TaskFilter.Deleted && model.Tasks.All(r => r.IsDeleted), "Deleted tab filters only tombstones", log);
            Capture(window, Path.Combine(output, "deleted-tab.png"));
            await Task.WhenAll(model.Tasks.Take(20).Select(r => model.DeleteRestoreAsync(r))); await Settle();
            Check(model.Tasks.Count == 0, "Concurrent restoration removes rows from Deleted", log);
            window.Width = 1100;
            using (var c = Database.Open(model.CurrentBook!.Path))
            {
                var now = DateTimeOffset.Now.ToUnixTimeMilliseconds(); var content = RichContent.FromText("虚拟化测试：屏幕之外的内容按需加载。");
                Database.Exec(c, """
                    WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM seq WHERE x<3200)
                    INSERT INTO Tasks(Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision) SELECT printf('%032x',x),NULL,$json,$text,0,$now-x,$now-x,NULL,1 FROM seq
                    """, ("$json", content.ToJson()), ("$text", content.PlainText), ("$now", now));
            }
            await model.SelectFilterAsync(TaskFilter.Open);
            for (int i = 0; i < 12; i++) await model.LoadPageAsync(PageDirection.Older);
            await Settle(); Check(model.Tasks.Count <= 2000 && model.HasNewer, "Sliding page cache evicts old pages", log);
            list.ScrollIntoView(model.Tasks[^1]); await Settle();
            var realized = MainWindow.Descendants<TaskCard>(list).Count(); Check(realized < 80, $"Virtualized controls bounded ({realized} realized)", log);
            var firstId = model.Tasks[0].Id; await model.LoadPageAsync(PageDirection.Newer); await Settle();
            Check(model.Tasks[0].Id != firstId && model.Tasks.Count <= 2000, "Evicted pages load backwards", log);
            for (int i = 0; i < 10; i++) await model.LoadPageAsync(PageDirection.Newer);
            await model.SelectFilterAsync(TaskFilter.Today); await Settle();
            Capture(window, Path.Combine(output, "dpi-125.png"), 1.25); Capture(window, Path.Combine(output, "06-dpi-150.png"), 1.5); Capture(window, Path.Combine(output, "07-dpi-200.png"), 2);
            File.WriteAllText(Path.Combine(output, "ui-smoke-results.json"), JsonSerializer.Serialize(new { Passed = true, Checks = log }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "ui-smoke-results.json"), JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString(), Checks = log }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
    }
    private static void Check(bool condition, string text, List<string> log) { if (!condition) throw new InvalidOperationException(text); log.Add(text); }
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(150); }
    private static void Capture(Window window, string path, double scale = 1)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
