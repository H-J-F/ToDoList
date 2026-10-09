using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ToDoList.App.Services;

// Real OS input, confined to the explicitly requested isolated theme diagnostic.
internal static class UiThemeDropdownChecks
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    private static void Move(Point p) => SetCursorPos((int)p.X, (int)p.Y);
    private static void EnsureForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        if (process != Environment.ProcessId) throw new InvalidOperationException("Theme diagnostic lost foreground; no input sent to other applications.");
    }
    private static void Click() { EnsureForeground(); mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0); }
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(260); }
    private static async Task Key(byte key) { EnsureForeground(); keybd_event(key, 0, 0, 0); keybd_event(key, 0, 2, 0); await Settle(); }

    internal static async Task RunAsync(MainWindow window, string output, Action<bool, string> check)
    {
        var selector = (ComboBox)window.FindName("ThemeSetting");
        var panel = (Border)window.FindName("SettingsPanel");
        panel.Visibility = Visibility.Visible;
        SetForegroundWindow(new WindowInteropHelper(window).Handle); window.Activate();
        var observations = new List<object>();
        var oldMotion = window.Model.Settings.ReduceMotion;
        try
        {
            foreach (var reduced in new[] { false, true })
            foreach (var index in new[] { 0, selector.Items.Count / 2, selector.Items.Count - 1 })
            foreach (var immediate in new[] { false, true })
            {
                window.Model.Settings.ReduceMotion = reduced; ThemeService.Apply(window.Model.Settings);
                selector.SelectedIndex = index; await Settle();
                SetForegroundWindow(new WindowInteropHelper(window).Handle);
                Move(selector.PointToScreen(new Point(selector.ActualWidth - 12, selector.ActualHeight / 2)));
                await Task.Delay(60); Click();
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                for (var attempt = 0; !selector.IsDropDownOpen && attempt < 30; attempt++) await Task.Delay(10);
                if (!immediate) await Settle();
                var popup = (Popup)selector.Template.FindName("Popup", selector);
                var border = (Border)selector.Template.FindName("DropDownBorder", selector);
                for (var attempt = 0; PresentationSource.FromVisual(border) == null && attempt < 30; attempt++) await Task.Delay(10);
                check(popup.IsOpen, $"Dropdown opens with OS mouse: reduced={reduced}, index={index}, immediate={immediate}");
                var scroll = MainWindow.Descendants<ScrollViewer>(border).First();
                var before = Snapshot();
                // Enter the viewport centre, then revisit its top and bottom fully visible rows.
                Move(scroll.PointToScreen(new Point(scroll.ActualWidth / 2, scroll.ActualHeight / 2)));
                await Settle();
                var after = Snapshot();
                observations.Add(new { reduced, index, immediate, before, after });
                check(Math.Abs(before.Offset - after.Offset) < 1 && Math.Abs(before.Top - after.Top) < 1 && Math.Abs(before.ItemTop - after.ItemTop) < 1,
                    $"Dropdown does not jump on pointer entry: reduced={reduced}, index={index}, immediate={immediate}");
                foreach (var y in new[] { 28d, scroll.ActualHeight - 28, scroll.ActualHeight / 2 })
                {
                    Move(window.PointToScreen(new Point(80, 110))); await Task.Delay(50);
                    Move(scroll.PointToScreen(new Point(scroll.ActualWidth / 2, y))); await Settle();
                    var repeated = Snapshot();
                    observations.Add(new { reduced, index, immediate, repeated });
                    check(Math.Abs(after.Offset - repeated.Offset) < 1 && Math.Abs(after.ItemTop - repeated.ItemTop) < 1,
                        $"Repeated pointer entry leaves rows fixed: reduced={reduced}, index={index}, y={y:F0}");
                }
                check(selector.SelectedIndex == index, "Hover does not select or persist another theme");
                await Key(0x1B);
                check(!selector.IsDropDownOpen && selector.SelectedIndex == index, "Escape closes dropdown without changing theme");

                Position Snapshot()
                {
                    border.UpdateLayout();
                    var item = (ComboBoxItem)selector.ItemContainerGenerator.ContainerFromIndex(index);
                    return new(border.PointToScreen(new Point()).Y, scroll.VerticalOffset, item.PointToScreen(new Point()).Y);
                }
            }
            selector.SelectedIndex = 0; await Settle();
            Move(selector.PointToScreen(new Point(selector.ActualWidth - 12, selector.ActualHeight / 2))); await Task.Delay(60); Click(); await Settle();
            var dropdown = (Border)selector.Template.FindName("DropDownBorder", selector);
            var viewer = MainWindow.Descendants<ScrollViewer>(dropdown).First();
            Move(viewer.PointToScreen(new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2)));
            await Settle();
            var start = viewer.VerticalOffset;
            EnsureForeground();
            mouse_event(0x0800, 0, 0, unchecked((uint)-120), 0); await Settle();
            check(viewer.VerticalOffset > start, "OS mouse wheel scrolls theme options");
            await Key(0x23); // End
            await Key(0x0D); // Enter
            check(!selector.IsDropDownOpen && selector.SelectedIndex == selector.Items.Count - 1, "End and Enter choose last theme");
            selector.Focus(); await Key(0x24); // Home
            await Key(0x28); // Down
            check(selector.SelectedIndex == 1, "Home and Down retain native keyboard navigation");
            Move(selector.PointToScreen(new Point(selector.ActualWidth - 12, selector.ActualHeight / 2))); await Task.Delay(60); Click(); await Settle();
            var chosen = (ComboBoxItem)selector.ItemContainerGenerator.ContainerFromIndex(2);
            chosen.BringIntoView(); await Settle();
            Move(chosen.PointToScreen(new Point(chosen.ActualWidth / 2, chosen.ActualHeight / 2))); await Task.Delay(60); Click(); await Settle();
            check(!selector.IsDropDownOpen && selector.SelectedIndex == 2 && window.Model.Library.LoadSettings().ThemeId == (string?)selector.SelectedValue,
                "OS mouse selects and persists the clicked theme");
        }
        finally
        {
            selector.IsDropDownOpen = false; panel.Visibility = Visibility.Collapsed;
            window.Model.Settings.ReduceMotion = oldMotion; ThemeService.Apply(window.Model.Settings);
            File.WriteAllText(Path.Combine(output, "dropdown-observations.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        }
    }
    private sealed record Position(double Top, double Offset, double ItemTop);
}
