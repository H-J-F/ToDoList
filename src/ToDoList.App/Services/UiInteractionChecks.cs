using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ToDoList.App.Controls;
using ToDoList.Core;

namespace ToDoList.App.Services;

internal static class UiInteractionChecks
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    private static void EnsureForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        if (process != Environment.ProcessId) throw new InvalidOperationException("Interaction check lost foreground; no input sent to other applications.");
    }
    private static async Task Keys(string keys)
    {
        EnsureForeground();
        byte code = keys switch { "^a" => 0x41, "^c" => 0x43, "^v" => 0x56, "^z" => 0x5A, "^y" => 0x59, "^{END}" => 0x23, _ => throw new ArgumentException(keys) };
        await Task.Run(async () =>
        {
            keybd_event(0x11, 0, 0, 0); await Task.Delay(15); keybd_event(code, 0, 0, 0);
            await Task.Delay(15); keybd_event(code, 0, 2, 0); keybd_event(0x11, 0, 2, 0);
        });
        await Task.Delay(40);
    }
    private static async Task Click(FrameworkElement element)
    {
        EnsureForeground();
        var p = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)p.X, (int)p.Y); mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0); await Task.Delay(250);
    }
    internal static IEnumerable<T> PopupElements<T>() where T : DependencyObject => PresentationSource.CurrentSources.OfType<HwndSource>()
        .Where(s => s.RootVisual != null).SelectMany(s => MainWindow.Descendants<T>(s.RootVisual)).ToArray();
    public static async Task RunAsync(MainWindow window)
    {
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        var checks = new List<string>(); var failures = new List<string>(); var observations = new List<object>();
        var previousClipboard = Clipboard.GetDataObject();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); } catch (Exception ex) { failures.Add(name + ": " + ex); }
            File.WriteAllText(Path.Combine(output, "interaction.json"), JsonSerializer.Serialize(new { Passed = failures.Count == 0, Checks = checks, Failures = failures, Observations = observations }, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Capture(string name)
        {
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(window);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var stream = File.Create(Path.Combine(output, name + ".png")); png.Save(stream);
        }
        try
        {
            if (window.Model.HasBook) throw new InvalidOperationException("Use empty test data.");
            var model = window.Model; var book = await model.Library.CreateAsync("Interactions", "2.3.1 实际鼠标键盘验证");
            await model.RefreshBooksAsync(); await model.OpenBookAsync(book);
            var a = await model.Repository!.AddProjectAsync("模块 A"); var b = await model.Repository.AddProjectAsync("模块 B"); var c = await model.Repository.AddProjectAsync("模块 C");
            await model.RefreshProjectsAsync();
            typeof(MainWindow).GetMethod("SyncSelectors", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false]);
            window.Topmost = true; window.Show(); ShowWindow(new WindowInteropHelper(window).Handle, 5);
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _); var thread = GetCurrentThreadId();
            var attached = foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
            try { window.Activate(); SetForegroundWindow(new WindowInteropHelper(window).Handle); }
            finally { if (attached) AttachThreadInput(thread, foregroundThread, false); }
            await Task.Delay(200); EnsureForeground();
            observations.Add(new { Binary = Environment.ProcessPath, Version = ApplicationLinks.Version });
            await Test("Real mouse hold/drag", async () =>
            {
                var list = (ListBox)window.FindName("ProjectList");
                var trace = new List<string>();
                string State() => "t=" + System.Diagnostics.Stopwatch.GetElapsedTime((long)typeof(MainWindow).GetField("_projectPressedAt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).TotalMilliseconds + ":" + string.Join(",", new[] { "_dragProject", "_projectDragging", "_dropTarget" }.Select(n => n + "=" + typeof(MainWindow).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)));
                list.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) => trace.Add("down:" + e.OriginalSource.GetType().Name + ":" + State())), true);
                list.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler((_, e) => trace.Add("lost:" + e.OriginalSource.GetType().Name + ":" + State())), true);
                list.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) => trace.Add("up:" + State())), true);
                list.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler((_, e) => { if (trace.Count < 25) trace.Add("move:" + State()); }), true);
                var timer = (System.Windows.Threading.DispatcherTimer)typeof(MainWindow).GetField("_projectHold", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                timer.Tick += (_, _) => trace.Add("tick:" + State());
                list.AddHandler(UIElement.DragOverEvent, new DragEventHandler((_, e) => trace.Add("over:" + e.Effects + ":" + e.OriginalSource.GetType().Name)), true);
                list.AddHandler(UIElement.DropEvent, new DragEventHandler((_, e) => trace.Add("drop:" + e.OriginalSource.GetType().Name)), true);
                var from = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(model.Projects.First(p => p.Id == a.Id));
                var to = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(model.Projects.First(p => p.Id == c.Id));
                var start = from.PointToScreen(new Point(50, from.ActualHeight / 2));
                var end = to.PointToScreen(new Point(50, to.ActualHeight - 5));
                await Task.Run(async () =>
                {
                    EnsureForeground(); SetCursorPos((int)start.X, (int)start.Y); mouse_event(2, 0, 0, 0, 0); await Task.Delay(1500);
                    for (int i = 1; i <= 10; i++) { EnsureForeground(); SetCursorPos((int)(start.X + (end.X - start.X) * i / 10), (int)(start.Y + (end.Y - start.Y) * i / 10)); await Task.Delay(60); }
                    mouse_event(4, 0, 0, 0, 0);
                });
                await Task.Delay(450); observations.Add(new { DragTrace = trace, Order = model.Projects.Select(p => p.Name).ToArray() }); Capture("mouse-drag");
                Check((await model.Repository.GetProjectsAsync()).Select(p => p.Id).SequenceEqual(new[] { b.Id, c.Id, a.Id }), "Mouse hold then drag A below C persisted B,C,A");
            });
            await Test("Real clipboard repeated multiline paste", async () =>
            {
                var editor = (RichEditor)window.FindName("DraftEditor"); var input = (RichTextBox)editor.FindName("Editor");
                InputMethod.SetIsInputMethodEnabled(input, false);
                model.Settings.Mode = "深色"; ThemeService.Apply(model.Settings);
                editor.SetContent(RichContent.FromText("测试\n第二行\n\n表情 👩‍💻")); await Click(input);
                var seed = editor.GetContent().PlainText;
                for (int i = 0; i < 60; i++)
                {
                    await Keys("^a"); await Keys("^c");
                    await Keys("^{END}"); await Keys("^v");
                    if (editor.GetContent().PlainText != seed + seed) observations.Add(new { Stage = "append", Iteration = i, Expected = seed + seed, Actual = editor.GetContent().PlainText, Clipboard = Clipboard.GetText(), Focus = Keyboard.FocusedElement?.GetType().Name });
                    Check(editor.GetContent().PlainText == seed + seed, "Clipboard multiline append " + i);
                    await Keys("^z"); await Keys("^y");
                    await Keys("^a"); await Keys("^v");
                    if (editor.GetContent().PlainText != seed) observations.Add(new { Iteration = i, Expected = seed, Actual = editor.GetContent().PlainText, Clipboard = Clipboard.GetText(), Focus = Keyboard.FocusedElement?.GetType().Name });
                    Check(editor.GetContent().PlainText == seed, "Clipboard multiline replacement " + i);
                }
                Check(editor.GetContent().Paragraphs.SelectMany(p => p.Runs).All(r => r.Color == null), "Repeated clipboard paste keeps automatic color"); Capture("repeated-paste");
                editor.SetContent(RichContent.FromText(""));
            });
            await Test("Expanded calendar screen bounds", async () =>
            {
                await Click((FrameworkElement)window.FindName("CalendarButton"));
                var picker = PopupElements<Wpf.Ui.Controls.CalendarDatePicker>().First(p => p.IsVisible);
                var scope = PopupElements<ComboBox>().First(p => p.IsVisible && System.Windows.Automation.AutomationProperties.GetAutomationId(p) == "DateScope");
                await Click(picker);
                var calendar = PopupElements<Calendar>().First(p => p.IsVisible);
                var top = scope.PointToScreen(new Point()); var date = picker.PointToScreen(new Point()); var cal = calendar.PointToScreen(new Point());
                var dpi = VisualTreeHelper.GetDpi(picker).DpiScaleX;
                observations.Add(new { Scope = new { X = top.X, Width = scope.ActualWidth }, Picker = new { X = date.X, Width = picker.ActualWidth }, Calendar = new { X = cal.X, Width = calendar.ActualWidth } });
                Capture("expanded-calendar");
                Check(Math.Abs(top.X - date.X) <= 1 && Math.Abs(scope.ActualWidth - picker.ActualWidth) <= 1, "Date button aligns to scope selector");
                Check(Math.Abs(date.X - cal.X) <= 1 && Math.Abs(picker.ActualWidth - calendar.ActualWidth) * dpi <= 1, "Expanded calendar left and right align to date button");
                System.Windows.Forms.SendKeys.SendWait("{ESC}");
            });
        }
        catch (Exception ex)
        {
            failures.Add(ex.ToString());
            File.WriteAllText(Path.Combine(output, "interaction.json"), JsonSerializer.Serialize(new { Passed = false, Checks = checks, Failures = failures, Observations = observations }));
        }
        finally
        {
            mouse_event(4, 0, 0, 0, 0);
            try { if (previousClipboard != null) Clipboard.SetDataObject(previousClipboard, true); } catch (ExternalException) { }
            Application.Current.Shutdown();
        }
    }
}
