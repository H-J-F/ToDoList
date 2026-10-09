using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Wpf.Ui.Tray.Controls;

namespace ToDoList.App.Services;

internal sealed class TrayService : IDisposable
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    private readonly NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private HwndSource[] _messageSources = [];
    private bool _disposed;
    internal ContextMenu Menu => _menu;
    internal bool IsRegistered => _icon.IsRegistered;
    internal nint MessageHandle => _messageSources.FirstOrDefault()?.Handle ?? 0;
    internal int IconId => _icon.Id;
    internal int RightClickCount { get; private set; }

    public TrayService(MainWindow window)
    {
        var open = new MenuItem { Header = "打开主窗口", Padding = new Thickness(10, 5, 14, 5) };
        var exit = new MenuItem { Header = "退出 ToDoList", Padding = new Thickness(10, 5, 14, 5) };
        _menu = new ContextMenu { UseLayoutRounding = true, SnapsToDevicePixels = true, FontWeight = FontWeights.Normal,
            Placement = PlacementMode.MousePoint, FontSize = 12, MinWidth = 132 };
        _menu.Opened += (_, _) =>
        {
            if (_menu.Parent is Popup popup) popup.PopupAnimation = PopupAnimation.None;
            if (_disposed || (Application.Current as App)?.ExitRequested == true) CloseMenu();
        };
        open.Click += (_, _) => { CloseMenu(); if (!_disposed) window.RestoreFromTray(); };
        exit.Click += (_, _) => { CloseMenu(); if (!_disposed) window.RequestExit(); };
        TextOptions.SetTextFormattingMode(_menu, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(_menu, TextRenderingMode.ClearType);
        _menu.Items.Add(open); _menu.Items.Add(new Separator()); _menu.Items.Add(exit);
        _icon = new NotifyIcon
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")),
            TooltipText = "ToDoList", Menu = _menu, MenuFontSize = 12,
            FocusOnLeftClick = false, MenuOnRightClick = false
        };
        _icon.LeftDoubleClick += ([System.Diagnostics.CodeAnalysis.NotNull] NotifyIcon sender, RoutedEventArgs e) =>
        {
            if (!_disposed && (Application.Current as App)?.ExitRequested != true) window.RestoreFromTray();
        };
        _icon.RightClick += ([System.Diagnostics.CodeAnalysis.NotNull] NotifyIcon sender, RoutedEventArgs e) => RightClickCount++;
        // 4.3.0 unregisters the shell icon but leaves its HwndSource alive.
        // Capture only sources created by this synchronous registration; dispose
        // them while WPF input and the Dispatcher are still fully operational.
        var before = PresentationSource.CurrentSources.OfType<HwndSource>().ToHashSet();
        try
        {
            _icon.Register();
            _messageSources = PresentationSource.CurrentSources.OfType<HwndSource>().Where(s => !before.Contains(s)).ToArray();
            if (!_icon.IsRegistered || _messageSources.Length == 0)
                throw new InvalidOperationException("无法注册 ToDoList 托盘图标。");
            foreach (var source in _messageSources) source.AddHook(OnTrayMessage);
        }
        catch { Dispose(); throw; }
    }

    private nint OnTrayMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // WPF UI 4.3.0 opens on WM_RBUTTONDOWN. Wait for the shell's
        // WM_RBUTTONUP notification, so releasing the button cannot dismiss
        // the popup we just opened. The library still owns icon registration.
        if (message >= 0x400 && lParam == 0x205 && !_disposed)
            _menu.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || (Application.Current as App)?.ExitRequested == true) return;
                CloseMenu(); SetForegroundWindow(hwnd); _menu.IsOpen = true;
            }));
        return 0;
    }

    public void UpdateText(string title)
    {
        if (_disposed) return;
        var text = "ToDoList · " + title;
        _icon.TooltipText = text.Length <= 63 ? text : text[..60] + "…";
    }

    internal void CloseMenu()
    {
        if (_menu.Parent is Popup popup) popup.PopupAnimation = PopupAnimation.None;
        Motion.Finish(_menu);
        if (_menu.IsMouseCaptureWithin) Mouse.Capture(null);
        _menu.IsOpen = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseMenu();
        _icon.MenuOnRightClick = false;
        _icon.Dispose();
        foreach (var source in _messageSources)
            if (!source.IsDisposed) { source.RemoveHook(OnTrayMessage); source.Dispose(); }
        _messageSources = [];
    }
}
