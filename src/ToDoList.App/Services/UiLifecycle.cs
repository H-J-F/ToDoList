using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ToDoList.App.Services;

internal static class UiLifecycle
{
    public static async Task RunAsync(MainWindow window)
    {
        var checks = new List<string>(); var failures = new List<string>();
        void Check(bool condition, string name) { if (!condition) failures.Add(name); else checks.Add(name); }
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        try
        {
            window.Close(); await Settle();
            Check(!window.IsVisible && !window.ShowInTaskbar, "Close hides window to tray");
            window.RestoreFromTray(); await Settle();
            Check(window.IsVisible && window.ShowInTaskbar && window.WindowState == WindowState.Normal, "Tray restore shows normal window");
            window.WindowState = WindowState.Maximized; await Settle(); window.Close(); await Settle(); window.RestoreFromTray(); await Settle();
            Check(window.WindowState == WindowState.Maximized, "Tray restore preserves maximized state");
            window.WindowState = WindowState.Minimized; await Settle(); window.RestoreFromTray(); await Settle();
            Check(window.WindowState == WindowState.Maximized, "Activation restores taskbar-minimized window");
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        File.WriteAllText(Path.Combine(output, "lifecycle.json"), JsonSerializer.Serialize(new { Passed = failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
        window.RequestExit();
    }
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(200); }
}
