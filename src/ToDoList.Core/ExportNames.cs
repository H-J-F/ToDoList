using System.Text.RegularExpressions;

namespace ToDoList.Core;

public static class ExportNames
{
    public static string FromTitle(string title)
    {
        var name = Regex.Replace(title, "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim().TrimEnd('.', ' ');
        if (name.Length == 0) name = "待办笔记";
        if (Regex.IsMatch(name.Split('.')[0], @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase)) name = "_" + name;
        return name;
    }
}
