using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ToDoList.App.Services;

internal static class ExplorerService
{
    private const int SwRestore = 9;
    private const int FlashwTray = 2;
    private const int FlashwTimerNoFg = 12;

    public static bool Reveal(string filePath)
    {
        filePath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(filePath)!;
        object? shellObject = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType != null)
            {
                shellObject = Activator.CreateInstance(shellType);
                dynamic shell = shellObject!;
                foreach (var candidate in shell.Windows())
                {
                    try
                    {
                        dynamic window = candidate;
                        var location = (string?)window.Document?.Folder?.Self?.Path;
                        if (location == null || !Path.GetFullPath(location).Equals(directory, StringComparison.OrdinalIgnoreCase)) continue;
                        dynamic item = window.Document.Folder.ParseName(Path.GetFileName(filePath));
                        if (item != null) window.Document.SelectItem(item, 1 | 4 | 8 | 16);
                        var hwnd = new IntPtr(Convert.ToInt64(window.HWND));
                        ShowWindow(hwnd, SwRestore);
                        if (!SetForegroundWindow(hwnd)) Flash(hwnd);
                        return true;
                    }
                    catch (Exception ex) when (ex is COMException or InvalidCastException or IOException) { }
                    finally { if (Marshal.IsComObject(candidate)) Marshal.FinalReleaseComObject(candidate); }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        finally { if (shellObject != null && Marshal.IsComObject(shellObject)) Marshal.FinalReleaseComObject(shellObject); }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
        return false;
    }

    private static void Flash(IntPtr hwnd)
    {
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = hwnd, Flags = FlashwTray | FlashwTimerNoFg, Count = 3 };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags; public uint Count; public uint Timeout; }
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
}
