using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ToDoList.App.Controls;
using ToDoList.Core;

namespace ToDoList.App.Services;

internal static partial class UiRevisionChecks
{
    private static async Task RunEditScrollAsync(MainWindow window)
    {
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        var checks = new List<string>(); var failures = new List<string>(); var samples = new List<object>();
        var completed = false;
        void Save() => File.WriteAllText(Path.Combine(output, "edit-scroll.json"), JsonSerializer.Serialize(new { Completed = completed, Passed = failures.Count == 0, Checks = checks, Failures = failures, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
        void Check(bool ok, string name) { (ok ? checks : failures).Add(name); Save(); }
        try
        {
            var model = window.Model;
            if (model.HasBook) throw new InvalidOperationException("An empty isolated data directory is required.");
            var book = await model.Library.CreateAsync("EditScroll", "底部编辑专项");
            await model.RefreshBooksAsync(); await model.OpenBookAsync(book);
            var project = await model.Repository!.AddProjectAsync("编辑模块"); await model.RefreshProjectsAsync();
            for (var i = 0; i < 240; i++) await model.Repository!.AddTaskAsync(RichContent.FromText($"滚动任务 {i}"), null);
            await model.SelectFilterAsync(TaskFilter.All); Invoke(window, "SyncSelectors", false); await Settle();
            var list = (ListBox)window.FindName("TaskList");
            var scroll = MainWindow.Descendants<ScrollViewer>(list).First();
            window.RestoreFromTray();
            foreach (var width in new[] { 850d, 1150d })
            foreach (var font in new[] { 12d, 18d })
            foreach (var longText in new[] { false, true })
            foreach (var reduce in new[] { false, true })
            {
                window.Width = width; window.Height = 600;
                model.Settings.FontSize = font; model.Settings.ReduceMotion = reduce; ThemeService.Apply(model.Settings);
                var row = model.Tasks.Last();
                var content = RichContent.FromText(longText ? string.Join("\n", Enumerable.Repeat("长文本自动换行与光标滚动验证。", 12)) : "末行短任务");
                row.Item = row.Item with { ContentJson = content.ToJson(), PlainText = content.PlainText };
                list.SelectedItem = row;
                scroll.ScrollToEnd(); await Settle(); await Task.Delay(800);
                var card = Card(window, row.Id); var host = (ContentControl)card.FindName("EditorHost");
                var frames = new List<(double Time, double Height, double Offset, double Top)>();
                var timer = Stopwatch.StartNew(); var brings = 0; var focuses = 0;
                EventHandler rendering = (_, _) => frames.Add((timer.Elapsed.TotalMilliseconds, host.ActualHeight, scroll.VerticalOffset, host.TransformToAncestor(scroll).Transform(new Point()).Y));
                RequestBringIntoViewEventHandler bring = (_, _) => brings++;
                KeyboardFocusChangedEventHandler focus = (_, _) => focuses++;
                card.AddHandler(FrameworkElement.RequestBringIntoViewEvent, bring, true);
                card.AddHandler(Keyboard.GotKeyboardFocusEvent, focus, true);
                CompositionTarget.Rendering += rendering;
                Save();
                try
                {
                    if (GetForegroundWindow() != new WindowInteropHelper(window).Handle) throw new InvalidOperationException("Test window lost foreground; no input sent.");
                    var body = (TextBlock)card.FindName("BodyText");
                    var bodyTop = body.TransformToAncestor(scroll).Transform(new Point()).Y;
                    var visibleMiddle = (Math.Max(0, bodyTop) + Math.Min(scroll.ViewportHeight, bodyTop + body.ActualHeight)) / 2;
                    var point = body.PointToScreen(new Point(body.ActualWidth / 2, visibleMiddle - bodyTop));
                    samples.Add(new { Click = true, width, font, longText, reduce, bodyTop, BodyHeight = body.ActualHeight, Viewport = scroll.ViewportHeight, Point = point.ToString(), Hit = window.InputHitTest(window.PointFromScreen(point))?.GetType().Name });
                    SetCursorPos((int)point.X, (int)point.Y);
                    mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0);
                    mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0);
                    await Task.Delay(3000);
                }
                finally { CompositionTarget.Rendering -= rendering; card.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, bring); card.RemoveHandler(Keyboard.GotKeyboardFocusEvent, focus); }
                var tail = frames.Where(f => f.Time > 2000).ToArray();
                samples.Add(new { width, font, longText, reduce, brings, focuses, Frames = frames.Select(f => new { f.Time, f.Height, f.Offset, f.Top }).ToArray() });
                Check(tail.Length >= 2 && tail.Max(f => f.Offset) - tail.Min(f => f.Offset) < 1 && tail.Max(f => f.Height) - tail.Min(f => f.Height) < 1 && tail.Max(f => f.Top) - tail.Min(f => f.Top) < 1, $"Settled bottom edit {width}/{font}/{longText}/{reduce}");
                Check(card.ActiveEditor != null && card.IsKeyboardFocusWithin && double.IsNaN(host.Height), "Editor remains interactive and naturally sized");
                card.ActiveProjectSelector!.SelectedValue = project.Id;
                SetDirtyContent(card.ActiveEditor!, "底部输入保存验证");
                await (Task)Invoke(window, "SaveRowEditAsync")!; await Settle();
                Check(model.Tasks.Single(t => t.Id == row.Id).Item.PlainText == "底部输入保存验证", "Bottom edit saves input");
                Check(model.Tasks.Single(t => t.Id == row.Id).Item.ProjectId == project.Id, "Bottom edit saves selected module");
                // Let the save snackbar leave the pointer target before the next
                // real double-click; it overlaps the final row in small windows.
                await Task.Delay(2300);
                scroll.ScrollToEnd(); await Settle(); card = Card(window, row.Id);
                Invoke(window, "Task_EditRequested", card, EventArgs.Empty); Invoke(window, "EndRowEdit"); await Settle();
                Check(card.ActiveEditor == null, "Immediate cancel invalidates expansion callbacks");
                scroll.ScrollToTop(); await Settle(); Check(scroll.VerticalOffset < 1, "Manual scrolling still works");
            }
            completed = true;
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally { Save(); Application.Current.Shutdown(); }
    }
}
