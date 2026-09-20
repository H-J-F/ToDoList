using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ToDoList.App.Controls;
using ToDoList.Core;

namespace ToDoList.App.Services;
internal static class UiPerformance
{
    public static async Task RunAsync(MainWindow window)
    {
        var output = Path.Combine(window.Model.Library.DataDirectory, "..", "ui-results.json");
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var process = Process.GetCurrentProcess();
            var startup = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
            var samples = new List<double>(); var model = window.Model; var list = (ListBox)window.FindName("TaskList");
            for (int i = 0; i < 30; i++)
            {
                var watch = Stopwatch.StartNew(); await model.SelectFilterAsync((TaskFilter)(i % 5));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); watch.Stop(); samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            await model.SelectFilterAsync(TaskFilter.Open);
            var initialMemory = process.WorkingSet64;
            var maxContainers = 0; var maxCache = 0; long maxContent = 0;
            for (int i = 0; i < 35; i++)
            {
                await model.LoadPageAsync(PageDirection.Older);
                list.ScrollIntoView(model.Tasks[^1]);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                maxContainers = Math.Max(maxContainers, MainWindow.Descendants<TaskCard>(list).Count());
                maxCache = Math.Max(maxCache, model.Tasks.Count); maxContent = Math.Max(maxContent, model.Tasks.Sum(t => t.EstimatedBytes));
            }
            process.Refresh(); samples.Sort(); var p95 = samples[(int)Math.Ceiling(samples.Count * .95) - 1];
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                StartupMilliseconds = Math.Round(startup, 2), TabSwitchP95Milliseconds = Math.Round(p95, 2),
                MaximumRealizedTaskControls = maxContainers, MaximumCachedTasks = maxCache, MaximumCachedContentBytes = maxContent,
                InitialWorkingSetBytes = initialMemory, WorkingSetAfterScrollingBytes = process.WorkingSet64,
                Passed = startup <= 2000 && p95 <= 300 && maxContainers < 80 && maxCache <= 2000 && maxContent <= 32 * 1024 * 1024
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { File.WriteAllText(output, ex.ToString()); }
        finally { Application.Current.Shutdown(); }
    }
}
