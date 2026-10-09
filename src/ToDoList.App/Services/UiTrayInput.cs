using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace ToDoList.App.Services;

// Real shell input for the opt-in isolated lifecycle diagnostic. The tray
// rectangle is resolved from this process's own HWND and notification ID.
internal static class UiTrayInput
{
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier
    { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier icon, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    internal static async Task OpenAsync(TrayService tray)
    {
        var marker = "ToDoList · 托盘验收 " + Environment.ProcessId;
        tray.UpdateText("托盘验收 " + Environment.ProcessId);
        await UiSettingsNavigationChecks.Settle();
        var id = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = tray.MessageHandle, Id = (uint)tray.IconId };
        var observation = "";
        bool TryPoint(out Point point)
        {
            point = default;
            var result = Shell_NotifyIconGetRect(ref id, out var r);
            observation = $"shell={result}, rect={r.Left},{r.Top},{r.Right},{r.Bottom}";
            // S_FALSE returns the CHEVRON rectangle, not the hidden icon's
            // rectangle. Resolve the uniquely labelled icon in the overflow UI.
            if (result == 1)
            {
                var overflowHandle = FindWindow("NotifyIconOverflowWindow", null);
                if (overflowHandle == 0 || !IsWindowVisible(overflowHandle)) return false;
                var icon = AutomationElement.FromHandle(overflowHandle).FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, marker));
                if (icon == null || icon.Current.IsOffscreen) return false;
                var bounds = icon.Current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width <= 0) return false;
                point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            }
            else
            {
                if (result != 0 || r.Right <= r.Left || r.Bottom <= r.Top) return false;
                point = new Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            }
            if (point.X < SystemParameters.VirtualScreenLeft || point.Y < SystemParameters.VirtualScreenTop ||
                point.X >= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth ||
                point.Y >= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) return false;
            try
            {
                var name = AutomationElement.FromPoint(point)?.Current.Name;
                observation += $", name={name}";
                return name == marker;
            }
            catch (ElementNotAvailableException) { return false; }
        }
        if (!TryPoint(out var point))
        {
            var taskbar = AutomationElement.FromHandle(FindWindow("Shell_TrayWnd", null));
            var buttons = taskbar.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>().Where(e => !e.Current.IsOffscreen).ToArray();
            var chevron = buttons.FirstOrDefault(e => e.Current.AutomationId.Contains("Chevron", StringComparison.OrdinalIgnoreCase) ||
                e.Current.AutomationId == "1502" || e.Current.Name.Contains("隐藏") || e.Current.Name.Contains("hidden", StringComparison.OrdinalIgnoreCase));
            if (chevron == null) throw new InvalidOperationException("Tray overflow button not found: " + string.Join(";", buttons.Select(e => e.Current.Name + "/" + e.Current.AutomationId)));
            for (var toggle = 0; toggle < 2 && !TryPoint(out point); toggle++)
            {
                var bounds = chevron.Current.BoundingRectangle;
                SetCursorPos((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                await Task.Delay(80);
                mouse_event(2, 0, 0, 0, 0); await Task.Delay(60); mouse_event(4, 0, 0, 0, 0);
                for (var attempt = 0; attempt < 10 && !TryPoint(out point); attempt++) await Task.Delay(100);
            }
            if (!TryPoint(out point)) throw new InvalidOperationException("This diagnostic's tray icon is not visible after opening overflow: " + observation + ", chevron=" + chevron.Current.BoundingRectangle);
        }
        var beforeClicks = tray.RightClickCount;
        var moved = SetCursorPos((int)point.X, (int)point.Y);
        await Task.Delay(100);
        var targetName = AutomationElement.FromPoint(point)?.Current.Name;
        GetCursorPos(out var actual);
        if ((Math.Abs(actual.X - point.X) > 2 || Math.Abs(actual.Y - point.Y) > 2) &&
            AutomationElement.FromPoint(new Point(actual.X, actual.Y))?.Current.Name != marker)
            throw new InvalidOperationException($"Pointer moved before tray input: expected={point}, actual={actual.X},{actual.Y}");
        var screenshotPath = System.IO.Path.Combine(((MainWindow)Application.Current.MainWindow).Model.Library.DataDirectory, "..", "tray-icon.png");
        using (var bitmap = new System.Drawing.Bitmap(240, 180))
        {
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen((int)point.X - 120, Math.Max(0, (int)point.Y - 30), 0, 0, bitmap.Size);
            bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
        }
        mouse_event(8, 0, 0, 0, 0); await Task.Delay(100); mouse_event(16, 0, 0, 0, 0);
        await UiSettingsNavigationChecks.Settle();
        if (!tray.Menu.IsOpen) throw new InvalidOperationException($"Physical tray right-click did not open the menu; registered={tray.IsRegistered}, hwnd={tray.MessageHandle}, point={point}, moved={moved}, target={targetName}, clicks={beforeClicks}->{tray.RightClickCount}, capture={System.Windows.Input.Mouse.Captured}.");
    }
}
