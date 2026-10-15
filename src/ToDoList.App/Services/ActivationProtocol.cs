using System.IO;
using Microsoft.Win32;

namespace ToDoList.App.Services;

internal static class ActivationProtocol
{
    public static bool IsActivation(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "todolist-desktop" && uri.Host == "activate" && uri.AbsolutePath is "" or "/" &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0 && uri.IsDefaultPort;

    public static bool EnsureRegistered()
    {
        // Isolated checks must never replace the user's application association.
        if (Environment.GetCommandLineArgs().Contains("--data-dir")) return false;
        var executable = Environment.ProcessPath;
        if (executable == null || !Path.GetFileName(executable).Equals("ToDoList.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\todolist-desktop");
            key.SetValue("", "URL:ToDoList activation"); key.SetValue("URL Protocol", "");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", "\"" + executable + "\" \"%1\""); return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }
}
