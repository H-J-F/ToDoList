namespace ToDoList.App.Services;

public static class ApplicationLinks
{
    public const string RepositoryUrl = "https://github.com/H-J-F/ToDoList";
    public static string Version => typeof(ApplicationLinks).Assembly.GetName().Version!.ToString(3);
    public static string BuildLabel => Version;
    public static string DisplayName => $"ToDoList  {Version}";
}
