using System.IO;

namespace ToDoList.App.Services;

internal static class AuthPaths
{
    internal static string Root
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--data-dir");
            return Path.Combine(index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : AppContext.BaseDirectory, "Auth");
        }
    }
    internal static bool IsIsolated => Environment.GetCommandLineArgs().Contains("--data-dir");
}
