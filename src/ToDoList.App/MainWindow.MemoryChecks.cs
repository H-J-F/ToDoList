using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ToDoList.App.Controls;
using ToDoList.App.Services;
using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.App;

public partial class MainWindow
{
    private sealed record MemorySample(string Stage, double ElapsedSeconds, long WorkingSetBytes, long PrivateBytes,
        long ManagedAllocatedBytes, long LastGcHeapBytes, long LastGcCommittedBytes, long LastGcFragmentedBytes,
        int Gen0Collections, int Gen1Collections, int Gen2Collections, int CachedTasks, long EstimatedContentBytes,
        int RealizedTaskControls, int Threads, int Handles);

    // Diagnostic only: never force a GC or trim the working set to improve a reading.
    internal async Task RunMemoryChecksAsync()
    {
        var output = Path.GetFullPath(Path.Combine(Model.Library.DataDirectory, "..", "memory-results.json"));
        var samples = new List<MemorySample>(); var checks = new List<string>();
        var clock = Stopwatch.StartNew(); string? failure = null; object? browserResult = null;
        using var process = Process.GetCurrentProcess();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(150); UpdateLayout(); }
        void Write(bool completed) => File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Completed = completed, Passed = completed && failure == null, Failure = failure,
            Runtime = Environment.Version.ToString(), ProcessId = Environment.ProcessId,
            Version = ApplicationLinks.BuildLabel, SyntheticTaskCount = 10_000, ForcedGc = false,
            Samples = samples, Checks = checks, Browser = browserResult,
            Limitations = new[] { "GC heap/committed values describe the last collection; ManagedAllocatedBytes is GC.GetTotalMemory(false).",
                "Private bytes and working set include native/runtime/graphics allocations; their difference from the managed heap is not an exact native allocation breakdown.",
                "Synthetic local data and synthetic intercepted login only; no live cloud account or long-duration leak proof." }
        }, new JsonSerializerOptions { WriteIndented = true }));
        void Sample(string stage)
        {
            var allocated = GC.GetTotalMemory(false); var gc = GC.GetGCMemoryInfo(); process.Refresh();
            samples.Add(new(stage, Math.Round(clock.Elapsed.TotalSeconds, 2), process.WorkingSet64, process.PrivateMemorySize64,
                allocated, gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), Model.Tasks.Count,
                Model.Tasks.Sum(t => t.EstimatedBytes), Descendants<TaskCard>(TaskList).Count(), process.Threads.Count, process.HandleCount));
            Write(false);
        }
        try
        {
            var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts")) + Path.DirectorySeparatorChar;
            Check(Model.Library.DataDirectory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && !Model.HasBook &&
                (await Model.Library.ListAsync()).Count == 0, "Fresh isolated artifacts workspace");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); _cloudTimer.Stop(); _calendar.Stop();
            await Settle(); Sample("empty-startup");
            var book = await Model.Library.CreateAsync("MemoryFixture", "一万条合成任务");
            await Task.Run(() =>
            {
                using var connection = Database.Open(book.Path); using var transaction = connection.BeginTransaction();
                var content = RichContent.FromText("合成内存测试：记录工作、整理想法、检查分页与富文本。🌱");
                Database.Exec(connection, """
                    WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM seq WHERE x<10000)
                    INSERT INTO Tasks(Id,ProjectId,ContentJson,PlainText,Status,CreatedAt,UpdatedAt,CompletedAt,Revision,DeletedAt,PreviousStatus,PreviousCompletedAt)
                    SELECT printf('%032x',x),NULL,$json,$text,x%4,$now-x*60000,$now-x*60000,
                        CASE WHEN x%4=2 THEN $now-x*60000 ELSE NULL END,1,
                        CASE WHEN x%4=3 THEN $now-x*60000 ELSE NULL END,CASE WHEN x%4=3 THEN 0 ELSE NULL END,NULL FROM seq;
                    """, ("$json", content.ToJson()), ("$text", content.PlainText), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                transaction.Commit();
            });
            var other = await Model.Library.CreateAsync("MemoryOther", "切换目标");
            await new TaskRepository(other.Path).AddTaskAsync(RichContent.FromText("第二本合成笔记"), null);
            await Model.RefreshBooksAsync(); await Model.OpenBookAsync(book); SyncSelectors(true);
            await Settle(); Sample("10000-first-page");
            for (var round = 1; round <= 3; round++)
            {
                for (var filter = 0; filter < 6; filter++) { await Model.SelectFilterAsync((TaskFilter)filter); await Settle(); }
                await Model.SelectFilterAsync(TaskFilter.All);
                for (var page = 0; page < 35; page++)
                {
                    await Model.LoadPageAsync(PageDirection.Older);
                    TaskList.ScrollIntoView(Model.Tasks[0]); await Settle();
                    Check(Model.Tasks.Count <= 2000 && Model.Tasks.Sum(t => t.EstimatedBytes) <= 32L * 1024 * 1024,
                        $"Bounded cache round {round} page {page}");
                }
                Sample($"round-{round}-paged");
                var row = Model.Tasks[0]; TaskList.ScrollIntoView(row); await Settle();
                Task_EditRequested(FindCard(row), EventArgs.Empty); await Task.Delay(400); await Settle();
                Check(_editingCard?.ActiveEditor != null, $"Shared editor opens round {round}");
                Sample($"round-{round}-editing"); EndRowEdit();
                await NavigateAsync(async () => { await Model.OpenBookAsync(other); SyncSelectors(true); }, true, bookSwitch: true);
                await NavigateAsync(async () => { await Model.OpenBookAsync(book); SyncSelectors(true); }, true, bookSwitch: true);
                await Settle(); Check(BookTransitionSnapshot.Source == null, $"Transition snapshot released round {round}");
                Sample($"round-{round}-switched");
            }
            Sample("idle-start");
            for (var seconds = 30; seconds <= 300; seconds += 30)
            { await Task.Delay(TimeSpan.FromSeconds(30)); await Settle(); Sample($"idle-{seconds}s"); }
            Sample("before-login");
            browserResult = await MeasureLoginBrowserAsync(() => Sample("login-open"));
            await Settle(); Sample("login-closed");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); Write(true);
            EndRowEdit(); DraftEditor.SetContent(RichContent.FromText("")); RequestExit();
        }
    }

    private async Task<object> MeasureLoginBrowserAsync(Action sample)
    {
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streams = new List<MemoryStream>(); var children = new List<Process>(); var readings = new List<object>();
        var login = new CloudLoginWindow(this, () => { }, async cache =>
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: cache);
            environment.BrowserProcessExited += (_, _) => exited.TrySetResult(); return environment;
        }, core =>
        {
            // Intercept every request, including subresources: never contact an account service.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                var stream = new MemoryStream(Encoding.UTF8.GetBytes("<html><meta charset='utf-8'><body>本地合成登录内存检查</body></html>"));
                streams.Add(stream); args.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
            };
            core.NavigationCompleted += (_, args) => { if (args.IsSuccess) navigation.TrySetResult(); };
        });
        try
        {
            await login.OpenAsync("https://www.weiyun.com/authorize/oauth?redirect_uri=http%3A%2F%2F127.0.0.1%3A54321%2Fcallback", CancellationToken.None);
            if (login.Browser.CoreWebView2 == null) throw new InvalidOperationException("WebView2 runtime unavailable for synthetic login measurement.");
            await navigation.Task.WaitAsync(TimeSpan.FromSeconds(20)); await Task.Delay(1000);
            foreach (var info in login.Browser.CoreWebView2.Environment.GetProcessInfos())
            {
                try
                {
                    var child = Process.GetProcessById(info.ProcessId); children.Add(child); child.Refresh();
                    readings.Add(new { ProcessId = child.Id, Kind = info.Kind.ToString(), WorkingSetBytes = child.WorkingSet64, PrivateBytes = child.PrivateMemorySize64 });
                }
                catch (ArgumentException) { }
            }
            sample(); var timer = Stopwatch.StartNew(); login.Complete();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
            while (children.Any(p => !p.HasExited) && timer.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
            var allExited = children.All(p => p.HasExited);
            if (!allExited) throw new InvalidOperationException("Synthetic login child processes did not exit after closing.");
            return new { Passed = true, AllObservedProcessesExited = allExited, ExitMilliseconds = timer.Elapsed.TotalMilliseconds, Processes = readings };
        }
        finally { login.Complete(); foreach (var child in children) child.Dispose(); foreach (var stream in streams) stream.Dispose(); }
    }
}
