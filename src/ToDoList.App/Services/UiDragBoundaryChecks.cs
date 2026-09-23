using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.App.Services;

// Exercises the production drag handlers against a rendered WPF list in isolated Data.
// Coordinates are supplied directly: this check does not inject global mouse/keyboard input.
internal static class UiDragBoundaryChecks
{
    public static async Task RunAsync(MainWindow window)
    {
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        var checks = new List<string>(); var failures = new List<string>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            var model = window.Model;
            if (model.HasBook) throw new InvalidOperationException("Drag checks require an empty test Data directory.");
            var book = await model.Library.CreateAsync("DragBoundaries", "拖动边界回归");
            await model.RefreshBooksAsync(); await model.OpenBookAsync(book);
            var a = await model.Repository!.AddProjectAsync("模块 A");
            var b = await model.Repository.AddProjectAsync("模块 B");
            var c = await model.Repository.AddProjectAsync("模块 C");
            await model.Repository.AddTaskAsync(RichContent.FromText("排序时保持编辑中的任务"), b.Id);
            await model.RefreshProjectsAsync(); Invoke("SyncSelectors", false);
            await model.SelectProjectAsync(b.Id); await model.SelectFilterAsync(TaskFilter.All); Invoke("SyncSelectors", false);
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            var list = (ListBox)window.FindName("ProjectList");
            ListBoxItem Item(ProjectInfo p) => (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(model.Projects.First(x => x.Id == p.Id));
            var slots = model.Projects.ToDictionary(p => p.Id ?? "all", p => new Rect(Item(p).TranslatePoint(new Point(), list), Item(p).RenderSize));
            Point Center(ListBoxItem item, double fraction = 0.5) { var r = slots[((ProjectInfo)item.DataContext).Id ?? "all"]; return new Point(r.Left + r.Width / 2, r.Top + r.Height * fraction); }
            void Begin() { Invoke("CancelProjectPress"); Set("_dragProject", a); Set("_projectPress", Center(Item(a))); Invoke("BeginProjectDrag"); }
            void Move(Point p) => Invoke("UpdateProjectDropTarget", p);
            async Task Release(Point p) => await (Task)Invoke("CompleteProjectPressAsync", p)!;
            var outside = new[] { new Point(-10, 10), new Point(list.ActualWidth + 10, 10), new Point(10, -10), new Point(10, list.ActualHeight + 10), new Point(-1000, -1000) };
            Check(list.InputHitTest(outside[0]) == null, "Real WPF hit test returns null outside captured list (original crash trigger)");
            foreach (var point in outside)
            {
                Begin(); Move(Center(Item(b)));
                Check(Field("_dropTarget") is ListBoxItem, "Valid target before leaving " + point);
                Move(point);
                Check(Field("_dropTarget") == null && Item(b).ReadLocalValue(Control.BorderThicknessProperty) == DependencyProperty.UnsetValue, "Outside point clears target and highlight " + point);
                await Release(point);
                Check(Field("_dragProject") == null && !(bool)Field("_projectDragging")!, "Outside release resets drag " + point);
            }
            Check((await model.Repository.GetProjectsAsync()).Select(p => p.Id).SequenceEqual(new[] { a.Id, b.Id, c.Id }), "All outside releases leave database order unchanged");
            Begin(); Move(Center(Item(b)));
            await Release(outside[0]);
            Check((await model.Repository.GetProjectsAsync()).First().Id == a.Id, "Outside release without preceding move cannot use stale target");
            Begin(); Move(Center(Item(b))); Move(Center(Item(model.Projects.First(p => p.Id == null))));
            Check(Field("_dropTarget") == null, "All modules is not a drop target");
            Move(Center(Item(a))); Check(Field("_dropTarget") == null, "Source module is not a drop target");
            var blank = new Point(list.ActualWidth / 2, list.ActualHeight - 2);
            Move(blank); Check(Field("_dropTarget") == null, "Blank list area clears target");
            Move(Center(Item(b))); Invoke("CancelProjectPress"); Move(Center(Item(c)));
            Check(Field("_dropTarget") == null && Field("_dragProject") == null, "Move after cancellation cannot restore a target");
            Begin();
            Check(Field("_projectPreview") is ProjectDragPreview preview && preview.GhostOpacity > 0 && preview.GhostOpacity < 1 && Item(a).Opacity == 0, "Hold creates translucent ghost and hides original row");
            var pointer = Center(Item(c), 0.75); Move(pointer);
            var ghost = (ProjectDragPreview)Field("_projectPreview")!;
            var ghostBefore = ghost.GhostPosition; Move(pointer + new Vector(12, 0));
            Check(Math.Abs(ghost.GhostPosition.X - ghostBefore.X - 12) < 0.01, "Ghost follows pointer offset exactly");
            Check(Item(b).RenderTransform is TransformGroup group && group.Children.OfType<TranslateTransform>().Any(t => t.HasAnimatedProperties), "Crossing rows starts an actual displacement animation");
            await Task.Delay(45);
            var middleOffset = Item(b).RenderTransform.Transform(new Point()).Y;
            Check(middleOffset < -0.1 && middleOffset > -(Item(a).ActualHeight + Item(a).Margin.Top + Item(a).Margin.Bottom), "Rendered displacement passes through intermediate positions");
            Capture(window, Path.Combine(output, "drag-preview-middle.png"));
            await Task.Delay(175);
            Check(Item(b).RenderTransform.Transform(new Point()).Y < -1 && Item(c).RenderTransform.Transform(new Point()).Y < -1, "Intermediate rows move upward to open insertion gap");
            Capture(window, Path.Combine(output, "drag-preview.png"));
            for (var i = 0; i < 100; i++) { Move(Center(Item(c), 0.75)); Move(outside[i % outside.Length]); }
            Move(Center(Item(c), 0.75));
            Check(Field("_dropTarget") == Item(c), "100 exit/reentry cycles still allow a valid target");
            // Keep both editors alive and watch for unintended refresh/selection changes.
            var draft = (Controls.RichEditor)window.FindName("DraftEditor");
            draft.SetContent(RichContent.FromText("底部草稿保留"));
            var row = model.Tasks.Single(); var card = window.FindCard(row)!;
            Invoke("Task_EditRequested", card, EventArgs.Empty);
            var activeEditor = card.ActiveEditor!; activeEditor.PastePlainText("未保存修改");
            var editContent = activeEditor.GetContent().ToJson();
            var draftSelector = (ComboBox)window.FindName("DraftProject");
            var selected = list.SelectedItem; var draftSelected = draftSelector.SelectedItem;
            var epoch = model.QueryEpoch; var taskEvents = 0; var resetEvents = 0; var busyEvents = 0; var selectionEvents = 0;
            model.Tasks.CollectionChanged += (_, _) => taskEvents++;
            model.Projects.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resetEvents++; };
            model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.IsBusy)) busyEvents++; };
            list.SelectionChanged += (_, _) => selectionEvents++;
            await Release(Center(Item(c), 0.75));
            var persisted = await new TaskRepository(model.CurrentBook!.Path).GetProjectsAsync();
            Check(persisted.Select(p => p.Id).SequenceEqual(new[] { b.Id, c.Id, a.Id }), "Reentry drop persists B/C/A through independent database connection");
            Check(model.Projects.Where(p => p.Id != null).Select(p => p.Id).SequenceEqual(persisted.Select(p => p.Id)), "Sidebar reflects persisted reorder");
            Check(taskEvents == 0 && resetEvents == 0 && busyEvents == 0 && model.QueryEpoch == epoch, "Reorder emits no task reload, collection reset or global busy changes");
            Check(selectionEvents == 0 && ReferenceEquals(list.SelectedItem, selected) && ReferenceEquals(draftSelector.SelectedItem, draftSelected), "Selected module and draft assignment retain identity without selection events");
            Check(ReferenceEquals(model.Tasks.Single(), row) && ReferenceEquals(window.FindCard(row), card) && ReferenceEquals(card.ActiveEditor, activeEditor) && activeEditor.GetContent().ToJson() == editContent, "Existing task editor and unsaved content survive reorder unchanged");
            Check(draft.GetContent().PlainText == "底部草稿保留", "Bottom draft remains unchanged");
            Invoke("EndRowEdit");
            window.UpdateLayout();
            slots = model.Projects.ToDictionary(p => p.Id ?? "all", p => new Rect(Item(p).TranslatePoint(new Point(), list), Item(p).RenderSize));
            Begin(); Move(Center(Item(b), 0.25)); await Task.Delay(220);
            Check(Item(b).RenderTransform.Transform(new Point()).Y > 1 && Item(c).RenderTransform.Transform(new Point()).Y > 1, "Upward drag moves intermediate rows down to open gap");
            Capture(window, Path.Combine(output, "drag-preview-up.png"));
            Invoke("CancelProjectPress");
            Check(Item(a).Opacity == 1 && Field("_projectPreview") == null && Item(b).RenderTransform.Transform(new Point()).Y == 0, "Cancellation removes ghost and restores original rows");
            Check((await model.Repository.GetProjectsAsync()).Select(p => p.Id).SequenceEqual(new[] { b.Id, c.Id, a.Id }), "Cancelled preview never writes its temporary order");
            Begin(); Move(Center(Item(b), 0.25)); await Release(Center(Item(b), 0.25));
            Check((await model.Repository.GetProjectsAsync()).Select(p => p.Id).SequenceEqual(new[] { a.Id, b.Id, c.Id }), "Upward drop persists A/B/C");
            await CheckCalendars(window, output, Check);
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            Invoke("CancelProjectPress");
            File.WriteAllText(Path.Combine(output, "drag-boundaries.json"), JsonSerializer.Serialize(new { Passed = failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
        }
    }

    private static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }

    private static async Task CheckCalendars(MainWindow owner, string output, Action<bool, string> check)
    {
        var form = new DateSelectionForm(true, new(DateScope.Day, DateTime.Today, DateTime.Today));
        var host = new Window { Owner = owner, Content = form.View, Width = 340, Height = 220, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
        try
        {
            host.Show();
            foreach (var mode in new[] { "浅色", "深色" })
            foreach (var width in new[] { 320d, 420d, 520d })
            {
                owner.Model.Settings.Mode = mode; ThemeService.Apply(owner.Model.Settings);
                form.View.Width = width; host.Width = width + 30; host.UpdateLayout();
                var picker = MainWindow.Descendants<Wpf.Ui.Controls.CalendarDatePicker>(form.View).First(p => p.IsVisible);
                foreach (var date in new[] { DateTime.Today, DateTime.Today.AddDays(DateTime.Today.Day == 1 ? 1 : -1) })
                {
                    picker.Date = date;
                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(picker);
                    ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                    await Task.Delay(160);
                    var calendar = UiInteractionChecks.PopupElements<Calendar>().First(c => c.IsVisible);
                    check(Math.Abs(calendar.ActualWidth - picker.ActualWidth) <= 1, "Calendar width still aligns at " + mode + width);
                    var days = MainWindow.Descendants<CalendarDayButton>(calendar).Where(d => d.IsVisible && (d.IsSelected || d.IsToday)).ToArray();
                    check(days.Any(d => d.IsSelected) && days.Any(d => d.IsToday), "Today and selected day are visible at " + mode + width + "/" + date.Day);
                    foreach (var day in days)
                    foreach (var part in new[] { "TodayBackground", "SelectedBackground" })
                    {
                        var shape = (FrameworkElement)day.Template.FindName(part, day);
                        var bounds = shape.TransformToAncestor(calendar).TransformBounds(new Rect(shape.RenderSize));
                        check(Math.Abs(bounds.Width - bounds.Height) < 0.1 && bounds.Width > 30, "Circular " + part + " " + mode + width + "/" + day.Content);
                    }
                    Capture(calendar, Path.Combine(output, "calendar-" + mode + width + ".png"));
                    picker.IsCalendarOpen = false;
                }
            }
        }
        finally { host.Close(); }
    }
}
