using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToDoList.Core;
using ToDoList.Storage;
using Wpf.Ui.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using MenuItem = System.Windows.Controls.MenuItem;
using Image = System.Windows.Controls.Image;

namespace ToDoList.App.Services;

// Opt-in integration check: OS input only targets this isolated process.
internal static class UiSettingsNavigationChecks
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    internal static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(250); }
    internal static async Task Click(FrameworkElement element)
    {
        var p = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        if (process != Environment.ProcessId) throw new InvalidOperationException("Diagnostic lost foreground; no input sent.");
        SetCursorPos((int)p.X, (int)p.Y); await Task.Delay(80);
        if (element is MenuItem)
        {
            var target = System.Windows.Automation.AutomationElement.FromPoint(p);
            var data = ((MainWindow)Application.Current.MainWindow).Model.Library.DataDirectory;
            File.AppendAllText(Path.Combine(data, "..", "menu-input.jsonl"), JsonSerializer.Serialize(new { p.X, p.Y, element.ActualWidth, element.ActualHeight,
                Element = ((MenuItem)element).Header?.ToString(), Target = target?.Current.Name,
                DirectlyOver = System.Windows.Input.Mouse.DirectlyOver?.GetType().FullName,
                Capture = System.Windows.Input.Mouse.Captured?.GetType().FullName, element.IsMouseOver }) + "\n");
        }
        mouse_event(2, 0, 0, 0, 0); await Task.Delay(60); mouse_event(4, 0, 0, 0, 0);
        await Settle();
    }
    private static async Task Key(byte key)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        if (process != Environment.ProcessId) throw new InvalidOperationException("Diagnostic lost foreground; no input sent.");
        keybd_event(key, 0, 0, 0); keybd_event(key, 0, 2, 0); await Settle();
    }
    private static async Task Choose(ComboBox box, int index, bool keyboard)
    {
        if (box.SelectedIndex == index) return;
        box.BringIntoView(); await Settle();
        await Click(box);
        if (!box.IsDropDownOpen) throw new InvalidOperationException(box.Name + " dropdown did not open");
        if (keyboard)
        {
            await Key(0x24);
            for (var i = 0; i < index; i++) await Key(0x28);
            await Key(0x0D);
        }
        else
        {
            var item = (ComboBoxItem)box.ItemContainerGenerator.ContainerFromIndex(index);
            item.BringIntoView(); await Settle(); await Click(item);
        }
    }
    public static async Task RunAsync(MainWindow window)
    {
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        var checks = new List<string>(); var failures = new List<string>(); var trace = new List<object>();
        var completed = false;
        void Check(bool condition, string name) { if (condition) checks.Add(name); else failures.Add(name); }
        void Save() => File.WriteAllText(Path.Combine(output, "settings-navigation.json"), JsonSerializer.Serialize(new { Completed = completed, Passed = completed && failures.Count == 0, Checks = checks, Failures = failures, Trace = trace }, new JsonSerializerOptions { WriteIndented = true }));
        var model = window.Model;
        var motion = (ComboBox)window.FindName("MotionSetting");
        var theme = (ComboBox)window.FindName("ThemeSetting");
        void Observe(string step)
        {
            var saved = model.Library.LoadSettings();
            trace.Add(new { Step = step, SelectedMotion = motion.SelectedIndex, SelectedTheme = theme.SelectedValue,
                model.Settings.ReduceMotion, model.Settings.ThemeId, AppliedTheme = ThemeService.Current.Id,
                AppliedMotion = ThemeService.ReduceMotion, SavedTheme = saved.ThemeId, SavedMotion = saved.ReduceMotion,
                SettingsVisible = ((FrameworkElement)window.FindName("SettingsPanel")).Visibility.ToString(),
                Sync = typeof(MainWindow).GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) });
            Save();
        }
        foreach (var selector in new[] { motion, theme })
            selector.SelectionChanged += (_, e) => trace.Add(new { Event = selector.Name, Source = e.OriginalSource.GetType().Name, Selected = selector.SelectedIndex });
        try
        {
            window.Topmost = true;
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _); var thread = GetCurrentThreadId();
            var attached = foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
            try { window.Activate(); SetForegroundWindow(new WindowInteropHelper(window).Handle); }
            finally { if (attached) AttachThreadInput(thread, foregroundThread, false); }
            if (Environment.GetCommandLineArgs().Contains("--settings-restart"))
            {
                Check(model.Settings.ThemeId == "classic-dark" && ThemeService.Current.Id == "classic-dark" &&
                    model.Settings.FontSize == 16 && model.Settings.Density == "紧凑" && !model.Settings.ReduceMotion,
                    "Theme, font, density and motion survive process restart");
                return;
            }
            ((FrameworkElement)window.FindName("SettingsPanel")).Visibility = Visibility.Visible; await Settle();
            Observe("initial");
            foreach (var keyboard in new[] { false, true })
            {
                await Choose(motion, 0, keyboard); Observe("standard");
                await Choose(motion, 1, keyboard); Observe("reduced");
                Check(model.Settings.ReduceMotion && model.Library.LoadSettings().ReduceMotion, "Reduced motion saved via " + (keyboard ? "keyboard" : "mouse"));
                await Choose(theme, 2, keyboard); Observe("dark");
                Check(model.Settings.ThemeId == "classic-dark" && ThemeService.Current.Id == "classic-dark" && model.Library.LoadSettings().ThemeId == "classic-dark", "Dark theme applies and saves after reduced motion");
                await Choose(motion, 0, keyboard); Observe("standard-again");
                Check(!model.Settings.ReduceMotion && !ThemeService.ReduceMotion && !model.Library.LoadSettings().ReduceMotion, "Standard motion can be restored");
                await Choose(theme, 1, keyboard); Observe("light");
                Check(model.Settings.ThemeId == "classic-light" && model.Library.LoadSettings().ThemeId == "classic-light", "Light theme still applies and saves");
            }
            await Choose((ComboBox)window.FindName("FontSetting"), 2, false);
            await Choose((ComboBox)window.FindName("DensitySetting"), 0, true);
            Check(model.Library.LoadSettings().FontSize == 16 && model.Library.LoadSettings().Density == "紧凑", "Font and density also persist through real input");
            theme.SelectedIndex = 2; motion.SelectedIndex = 1; theme.SelectedIndex = 3; motion.SelectedIndex = 0; theme.SelectedIndex = 2;
            await Settle(); Observe("coalesced");
            Check(model.Settings.ThemeId == "classic-dark" && !model.Settings.ReduceMotion && model.Library.LoadSettings().ThemeId == "classic-dark", "Rapid queued choices retain the last value of every field");
            using (var locked = new FileStream(Path.Combine(model.Library.DataDirectory, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                theme.SelectedIndex = 1; await Settle();
                Check(model.Settings.ThemeId == "classic-dark" && ThemeService.Current.Id == "classic-dark" && (string?)theme.SelectedValue == "classic-dark", "Save failure rolls the visible and applied values back");
                MainWindow.Descendants<ContentDialog>(window).Single().TemplateButtonCommand.Execute(ContentDialogButton.Close);
            }
            await Settle();
            await Choose(theme, 1, false); await Choose(theme, 2, true);
            Check(model.Library.LoadSettings().ThemeId == "classic-dark", "Settings can be saved after recovering from a write failure");
            var panel = (FrameworkElement)window.FindName("SettingsPanel");
            var hiding = Motion.HideAsync(panel); motion.SelectedIndex = 1; await Settle();
            Check(hiding.IsCompleted && panel.Visibility == Visibility.Collapsed, "Changing motion completes an in-flight panel close");
            panel.Visibility = Visibility.Visible; Motion.Reveal(panel); await Settle();
            await Choose(motion, 0, true);
            Check(panel.IsVisible && panel.Opacity == 1, "Settings panel is usable again after an interrupted animation");
            await Click((FrameworkElement)window.FindName("CloseSettingsButton"));
            await CheckBooksAsync(window, output, Check, trace);
            if (Environment.GetCommandLineArgs().Contains("--tray-native"))
            {
                window.Close(); await Settle();
                await UiTrayInput.OpenAsync(((App)Application.Current).Tray!);
                Check(true, "Physical tray right-click works after settings and book switching");
                window.Closed += (_, _) => { completed = true; Save(); };
                await Click((MenuItem)((App)Application.Current).Tray!.Menu.Items[2]);
            }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally { completed = true; Save(); window.RequestExit(); }
    }

    private static async Task CheckBooksAsync(MainWindow window, string output, Action<bool, string> check, List<object> trace)
    {
        var model = window.Model;
        if (model.HasBook) throw new InvalidOperationException("Book checks require empty isolated data.");
        var a = await model.Library.CreateAsync("SwitchA", "切换验收 A");
        var b = await model.Library.CreateAsync("SwitchB", "切换验收 B");
        var empty = await model.Library.CreateAsync("SwitchEmpty", "空笔记");
        foreach (var book in new[] { a, b })
        {
            var repository = new TaskRepository(book.Path);
            await repository.AddProjectAsync("模块 " + book.Name);
            for (var i = 0; i < 8; i++) await repository.AddTaskAsync(RichContent.FromText(book.Title + " · 任务 " + i), null);
        }
        await model.RefreshBooksAsync(); await model.OpenBookAsync(a);
        typeof(MainWindow).GetMethod("SyncSelectors", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [true]);
        await Settle();
        var selector = (ComboBox)window.FindName("BookSelector");
        var snapshot = (Image)window.FindName("BookTransitionSnapshot");
        var overlay = (FrameworkElement)window.FindName("BlockingOverlay");
        foreach (var reduced in new[] { false, true })
        {
            model.Settings.ReduceMotion = reduced; ThemeService.Apply(model.Settings); window.SyncSettings();
            foreach (var target in new[] { b, empty, a })
            {
                var frames = new List<(bool Busy, bool Overlay, bool Snapshot, double Opacity)>();
                var bitmaps = new List<BitmapSource>();
                void Frame(object? sender, EventArgs e)
                {
                    if (!model.IsSwitchingBook) return;
                    frames.Add((model.IsBusy, overlay.IsVisible, snapshot.IsVisible, snapshot.Opacity));
                    if (bitmaps.Count < 4)
                    {
                        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window); bitmap.Freeze(); bitmaps.Add(bitmap);
                    }
                }
                CompositionTarget.Rendering += Frame;
                try
                {
                    await Choose(selector, model.Books.ToList().FindIndex(book => book.Name == target.Name), false);
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (model.IsSwitchingBook && DateTime.UtcNow < deadline) await Task.Delay(20);
                }
                finally { CompositionTarget.Rendering -= Frame; }
                check(model.CurrentBook?.Name == target.Name && !model.IsBusy && !model.IsSwitchingBook, $"Book switch completes: {target.Name}, reduced={reduced}");
                check(frames.All(f => !f.Overlay), "Switch never displays the full-workspace busy overlay");
                check(reduced ? frames.All(f => !f.Snapshot) : frames.Any(f => f.Snapshot && f.Opacity > 0 && f.Opacity < 1),
                    reduced ? "Reduced mode updates without a transition" : "Standard mode presents intermediate cross-fade frames");
                check(target == empty ? model.Tasks.Count == 0 : model.Tasks.Count == 8 && model.Tasks.All(t => t.Item.PlainText.StartsWith(target.Title)), "Visible rows all belong to the new book");
                trace.Add(new { Book = target.Name, Reduced = reduced, Frames = frames.Select(f => new { f.Busy, f.Overlay, f.Snapshot, f.Opacity }) });
                for (var i = 0; i < bitmaps.Count; i++)
                {
                    using var file = File.Create(Path.Combine(output, $"switch-{target.Name}-{reduced}-{i}.png"));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmaps[i])); encoder.Save(file);
                }
            }
        }
        var oldRows = model.Tasks.Select(t => t.Id).ToArray();
        using (var gate = new ManualResetEventSlim())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = Database.RunAsync(b.Path, () => { entered.TrySetResult(); gate.Wait(); return true; });
            await entered.Task;
            try
            {
                var pending = model.OpenBookAsync(b);
                await Task.Delay(350);
                check(model.CurrentBook == a && model.Tasks.Select(t => t.Id).SequenceEqual(oldRows), "Old book and rows stay intact during a deliberately slow load");
                gate.Set(); await blocker; await pending;
            }
            finally { gate.Set(); }
        }
        var invalid = new BookInfo("Invalid", "损坏笔记", Path.Combine(model.Library.DataDirectory, "invalid.db"));
        File.WriteAllText(invalid.Path, "not a database");
        try { await model.OpenBookAsync(invalid); check(false, "Broken database must fail"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { check(model.CurrentBook == b && model.Tasks.Count == 8 && model.Library.LoadSettings().LastBook == b.Name, "Load failure preserves the previous book, rows and LastBook"); }
        typeof(MainWindow).GetMethod("SyncSelectors", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [true]);
        var draft = (ToDoList.App.Controls.RichEditor)window.FindName("DraftEditor");
        draft.SetContent(RichContent.FromText("取消切换时保留草稿"));
        selector.SelectedItem = model.Books.First(book => book.Name == empty.Name); await Settle();
        MainWindow.Descendants<ContentDialog>(window).Single().TemplateButtonCommand.Execute(ContentDialogButton.Close); await Settle();
        check(model.CurrentBook == b && ((BookInfo)selector.SelectedItem).Name == b.Name && draft.GetContent().PlainText == "取消切换时保留草稿", "Cancelling a dirty-book switch preserves book, selection and draft");
        draft.SetContent(RichContent.FromText(""));
        model.Settings.ReduceMotion = false; ThemeService.Apply(model.Settings); model.SaveSettings(); window.SyncSettings();
    }
}
