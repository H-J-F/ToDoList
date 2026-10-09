using System.IO;
using System.Text.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ToDoList.App.Services;

internal static class UiLifecycle
{
    public static async Task RunAsync(MainWindow window)
    {
        var checks = new List<string>(); var failures = new List<string>();
        void Check(bool condition, string name) { if (!condition) failures.Add(name); else checks.Add(name); }
        var output = Path.GetFullPath(Path.Combine(window.Model.Library.DataDirectory, ".."));
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--reduced-motion")) { window.Model.Settings.ReduceMotion = true; ThemeService.Apply(window.Model.Settings); }
        Window? auxiliary = null;
        var initialState = window.WindowState;
        void Save(bool completed) => File.WriteAllText(Path.Combine(output, "lifecycle.json"), JsonSerializer.Serialize(new { Completed = completed, Passed = failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            window.Close(); await Settle();
            Check(!window.IsVisible && !window.ShowInTaskbar, "Close hides window to tray");
            window.RestoreFromTray(); await Settle();
            Check(window.IsVisible && window.ShowInTaskbar && window.WindowState == initialState, "Tray restore preserves initial window state");
            window.WindowState = WindowState.Maximized; await Settle(); window.Close(); await Settle(); window.RestoreFromTray(); await Settle();
            Check(window.WindowState == WindowState.Maximized, "Tray restore preserves maximized state");
            window.WindowState = WindowState.Minimized; await Settle(); window.RestoreFromTray(); await Settle();
            Check(window.WindowState == WindowState.Maximized, "Activation restores taskbar-minimized window");
            if (args.Contains("--exit-extra-window"))
            {
                auxiliary = new Window { Title = "Lifecycle auxiliary window", ShowInTaskbar = false, Style = null };
                auxiliary.Show(); auxiliary.Hide();
                Check(Application.Current.Windows.Cast<Window>().Contains(auxiliary), "Hidden auxiliary window exists before exit");
            }
            if (!args.Contains("--exit-visible")) { window.Close(); await Settle(); }
            Check(window.IsVisible == args.Contains("--exit-visible"), "Exit starts in requested visibility state");
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        Save(false);
        window.Closed += (_, _) =>
        {
            Check(((App)Application.Current).ExitRequested, "Exit was approved before main window closed");
            Check(auxiliary == null || Application.Current.Windows.Cast<Window>().Contains(auxiliary), "Main window close does not rely on last-window shutdown");
            Save(true);
        };
        // Exercise the real tray menu and its Click handler, without depending
        // on Explorer's overflow placement or touching another instance's icon.
        var app = (App)Application.Current;
        var tray = app.Tray!;
        var menu = tray.Menu;
        void Input(object sender, System.Windows.Input.MouseButtonEventArgs e) => File.AppendAllText(Path.Combine(output, "menu-events.txt"), $"{e.RoutedEvent.Name}: {e.ChangedButton}, source={e.OriginalSource}, handled={e.Handled}, open={menu.IsOpen}\n");
        menu.AddHandler(System.Windows.Input.Mouse.PreviewMouseDownEvent, new System.Windows.Input.MouseButtonEventHandler(Input), true);
        menu.AddHandler(System.Windows.Input.Mouse.PreviewMouseUpEvent, new System.Windows.Input.MouseButtonEventHandler(Input), true);
        menu.Closed += (_, _) => File.AppendAllText(Path.Combine(output, "menu-events.txt"), "Closed\n");
        app.Exit += (_, _) =>
        {
            Check(!tray.IsRegistered && tray.MessageHandle == 0 && !menu.IsOpen, "Application exit removes tray icon, message source and menu");
            Save(true);
        };
        try
        {
            if (args.Contains("--tray-native"))
            {
                await UiTrayInput.OpenAsync(tray);
                Check(menu.IsOpen, "OS right-click opens this process's shell tray menu");
                tray.CloseMenu(); await Settle();
                await UiTrayInput.OpenAsync(tray);
                Check(menu.IsOpen, "Tray menu reopens after closing");
                Save(false);
                var exit = (MenuItem)menu.Items[2];
                var clicked = false;
                exit.Click += (_, _) => { clicked = true; Check(true, "Exit menu receives physical mouse click"); Save(false); };
                await UiSettingsNavigationChecks.Click(exit);
                if (!clicked)
                {
                    failures.Add($"Physical exit click was not delivered; menuOpen={menu.IsOpen}, itemVisible={exit.IsVisible}, mouseOver={exit.IsMouseOver}");
                    Save(false); window.RequestExit();
                }
            }
            else
            {
                menu.IsOpen = true; await Settle();
                Check(menu.IsOpen, "Real tray context menu opens before exit");
                Save(false);
                if (args.Contains("--exit-manual")) return;
                ((MenuItem)menu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); Save(false); window.RequestExit(); }
    }
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(200); }
}
