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
    private SingleInstanceCoordinator? _instance;
    private TrayService? _tray;
    private bool _shutdownRequested;
    internal TrayService? Tray => _tray;
    internal bool ExitRequested { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("zh-CN")));
        if (e.Args.Contains("--ui-themes") && (e.Args.Length < 2 || e.Args[0] != "--data-dir"))
        { ExitRequested = true; Shutdown(2); return; }
        var isolatedCheck = e.Args.Any(a => a is "--ui-smoke" or "--ui-themes" or "--ui-revision" or "--ui-drag-boundaries" or "--ui-interaction" or "--ui-taskfixes" or "--ui-features" or "--ui-typography") && e.Args.Length >= 2 && e.Args[0] == "--data-dir";
        var lifecycleCheck = e.Args.Contains("--ui-lifecycle") && e.Args.Length >= 2 && e.Args[0] == "--data-dir";
        // A stable, isolated name lets lifecycle checks verify release on restart
        // without activating or closing the user's running application.
        var suffix = lifecycleCheck
            ? ".Lifecycle." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(e.Args[1]).ToUpperInvariant())))[..16]
            : isolatedCheck ? ".TaskFixes." + Environment.ProcessId : "";
        _instance = new SingleInstanceCoordinator(suffix);
        if (!_instance.TryAcquire())
        {
            if (!SingleInstanceCoordinator.ActivateExisting(suffix)) MessageBox.Show("ToDoList 已在运行，但暂时无法激活窗口。", "ToDoList");
            else
            {
                // A global singleton can otherwise silently redirect a newly installed
                // build to an older executable, making an upgrade appear ineffective.
                var current = System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion;
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("ToDoList").Concat(System.Diagnostics.Process.GetProcessesByName("ToDoList.App")))
                {
                    using (process)
                    {
                        if (process.Id == Environment.ProcessId) continue;
                        try
                        {
                            var path = process.MainModule?.FileName;
                            if (path == null) continue;
                            var existing = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion;
                            if (existing != current)
                                MessageBox.Show($"当前已有另一版本正在运行：\n{path}\n\n本次 {ApplicationLinks.BuildLabel} 尚未启动。请先从旧版托盘菜单退出，再打开新版。", "当前运行版本不同");
                            break;
                        }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    }
                }
            }
            ExitRequested = true; Shutdown(); return;
        }
        var data = Path.Combine(AppContext.BaseDirectory, "Data");
        if (e.Args.Length >= 2 && e.Args[0] == "--data-dir") data = Path.GetFullPath(e.Args[1]);
        try
        {
            var library = new BookLibrary(data); library.EnsureWritable(); var settings = library.LoadSettings();
            if (!File.Exists(Path.Combine(data, "settings.json"))) settings.ReduceMotion = !SystemParameters.ClientAreaAnimation;
            ThemeService.Apply(settings); InteractionMotion.Register();
            var window = new MainWindow(library, settings); MainWindow = window;
            window.Closed += (_, _) =>
            {
                // Close has already passed the pending-edit checks. Other hidden
                // WPF windows must not keep the application alive.
                if (_shutdownRequested) return;
                _shutdownRequested = true;
                Dispatcher.BeginInvoke(new Action(() => Shutdown()));
            };
            window.Show();
            _tray = new TrayService(window); _tray.UpdateText(window.Model.BookTitle);
            window.Model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(window.Model.BookTitle)) _tray?.UpdateText(window.Model.BookTitle); };
            _instance.Listen(() => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ExitRequested && !_shutdownRequested) window.RestoreFromTray();
            })));
        }
        catch (Exception ex)
        {
            if (e.Args.Any(a => a is "--ui-themes" or "--ui-revision" or "--ui-drag-boundaries" or "--ui-interaction" or "--ui-taskfixes" or "--ui-smoke" or "--ui-perf" or "--ui-demo" or "--ui-typography" or "--ui-features" or "--ui-lifecycle"))
            {
                var report = Path.Combine(data, "..", "startup-error.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(report)!); File.WriteAllText(report, ex.ToString());
            }
            else MessageBox.Show("无法启动 ToDoList。\n\n" + ex.GetBaseException().Message, "ToDoList");
            ExitRequested = true; Shutdown(1);
        }
    }
    internal void BeginExit() => ExitRequested = true;
    internal void PrepareExit() => _tray?.Dispose();
    internal void CancelExit() => ExitRequested = false;
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e) { ExitRequested = true; base.OnSessionEnding(e); }
    protected override void OnExit(ExitEventArgs e) { ExitRequested = true; _tray?.Dispose(); _instance?.Dispose(); base.OnExit(e); }
}
