using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        var runtime = Directory.Exists(root) && Directory.GetDirectories(root).Select(Path.GetFileName).Any(name => { Version version; return Version.TryParse(name, out version) && version.Major >= 10; });
        if (!runtime)
        {
            var answer = MessageBox.Show("ToDoList 精简版需要安装 .NET 10 Desktop Runtime x64。\n\n是否打开微软下载页面？", "缺少运行环境", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) Process.Start("https:" + "//dotnet.microsoft.com/zh-cn/download/dotnet/10.0");
            return;
        }
        var app = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ToDoList.App.exe");
        if (File.Exists(app))
        {
            var start = new ProcessStartInfo(app, string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(Quote)));
            start.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory; Process.Start(start);
        }
        else MessageBox.Show("找不到 ToDoList.App.exe。", "ToDoList", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
}
