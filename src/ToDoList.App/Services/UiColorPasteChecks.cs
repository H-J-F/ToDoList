using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace ToDoList.App.Services;

internal static partial class UiRevisionChecks
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);

    private static async Task RunColorPasteAsync(MainWindow window)
    {
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        var checks = new List<string>(); var failures = new List<string>(); var completed = false;
        void Save() => File.WriteAllText(Path.Combine(output, "color-paste.json"), JsonSerializer.Serialize(new { Completed = completed, Passed = failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
        void Check(bool ok, string name) { (ok ? checks : failures).Add(name); Save(); }
        bool HasError() => MainWindow.Descendants<Wpf.Ui.Controls.ContentDialog>(window).Any(d => d.IsVisible);
        void EnsureForeground()
        {
            if (GetForegroundWindow() != new WindowInteropHelper(window).Handle) throw new InvalidOperationException("Test window lost foreground; no input sent.");
        }
        async Task<ContextMenu> OpenMenu(TextBox box)
        {
            EnsureForeground();
            var point = box.PointToScreen(new Point(box.ActualWidth / 2, box.ActualHeight / 2));
            SetCursorPos((int)point.X, (int)point.Y);
            mouse_event(8, 0, 0, 0, 0); mouse_event(16, 0, 0, 0, 0); await Settle();
            return UiInteractionChecks.PopupElements<ContextMenu>().First(m => m.IsOpen);
        }
        try
        {
            Save();
            var model = window.Model;
            if (model.HasBook) throw new InvalidOperationException("An empty isolated data directory is required.");
            var book = await model.Library.CreateAsync("ColorPaste", "颜色粘贴验证");
            await model.RefreshBooksAsync(); await model.OpenBookAsync(book);
            window.RestoreFromTray(); window.Height = 900;
            Invoke(window, "Settings_Click", window, new RoutedEventArgs()); await Settle();
            foreach (var name in new[] { "Open", "Verification", "Completed" })
            {
                var box = (TextBox)window.FindName(name + "ColorSetting");
                box.BringIntoView(); box.Focus(); box.Clear(); Clipboard.SetText("#12ab34"); await Settle();
                var menu = await OpenMenu(box);
                Check(menu.IsOpen && !HasError(), name + ": empty input opens native right-click menu without error");
                var paste = menu.Items.OfType<MenuItem>().First(i => i.Command == ApplicationCommands.Paste);
                ((IInvokeProvider)new MenuItemAutomationPeer(paste).GetPattern(PatternInterface.Invoke)).Invoke(); await Settle();
                Check(box.Text == "#12ab34" && !HasError(), name + ": native Paste command inserts color");
                ((ComboBox)window.FindName("ModeSetting")).Focus(); await Settle();
                Check(await model.Repository!.GetSettingAsync("StatusColor." + name) == "#12AB34", name + ": leaving field persists normalized color");
                box.Focus(); box.Clear(); Clipboard.SetText("#abcdef"); EnsureForeground();
                keybd_event(0x11, 0, 0, 0); keybd_event(0x56, 0, 0, 0); keybd_event(0x56, 0, 2, 0); keybd_event(0x11, 0, 2, 0); await Settle();
                Check(box.Text == "#abcdef" && !HasError(), name + ": Ctrl+V still works");
                box.Clear(); menu = await OpenMenu(box); menu.IsOpen = false; await Settle();
                Check(box.IsKeyboardFocusWithin && !HasError(), name + ": dismissing menu keeps empty input editable");
                box.Text = "#12";
                ((ComboBox)window.FindName("ModeSetting")).Focus(); await Settle();
                Check(HasError(), name + ": invalid value still reports error on actual leave");
                box.Text = "#abcdef";
                await Choose(window, Wpf.Ui.Controls.ContentDialogButton.Close);
                box.Focus(); ((ComboBox)window.FindName("ModeSetting")).Focus(); await Settle();
                Check(await model.Repository.GetSettingAsync("StatusColor." + name) == "#ABCDEF", name + ": corrected value persists");
            }
            await model.OpenBookAsync(book); window.SyncSettings();
            Check(new[] { "Open", "Verification", "Completed" }.All(n => ((TextBox)window.FindName(n + "ColorSetting")).Text == "#ABCDEF"), "Reopening book reloads all saved colors");
            completed = true;
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            Save();
            Application.Current.Shutdown();
        }
    }
}
