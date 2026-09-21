using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using ToDoList.App.Services;
using ToDoList.Storage;

namespace ToDoList.App;
public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("zh-CN")));
        var data = Path.Combine(AppContext.BaseDirectory, "Data");
        if (e.Args.Length >= 2 && e.Args[0] == "--data-dir") data = Path.GetFullPath(e.Args[1]);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..24];
        _instance = new Mutex(true, @"Local\ToDoList-" + key, out var created);
        if (!created) { MessageBox.Show("这份 ToDoList 已在运行，请从任务栏打开。", "ToDoList"); Shutdown(); return; }
        try
        {
            var library = new BookLibrary(data); library.EnsureWritable(); var settings = library.LoadSettings();
            if (!File.Exists(Path.Combine(data, "settings.json"))) settings.ReduceMotion = !SystemParameters.ClientAreaAnimation;
            ThemeService.Apply(settings); InteractionMotion.Register();
            var window = new MainWindow(library, settings); MainWindow = window; window.Show();
        }
        catch (Exception ex)
        {
            if (e.Args.Any(a => a is "--ui-smoke" or "--ui-perf" or "--ui-demo" or "--ui-typography" or "--ui-features"))
            {
                var report = Path.Combine(data, "..", "startup-error.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(report)!); File.WriteAllText(report, ex.ToString());
            }
            else MessageBox.Show("无法启动 ToDoList。\n\n" + ex.GetBaseException().Message, "ToDoList");
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
