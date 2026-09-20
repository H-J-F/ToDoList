using System.Diagnostics;
using System.Text.Json;
using ToDoList.Core;
using ToDoList.Storage;

var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/benchmarks");
Directory.CreateDirectory(output);
var sizes = args.Length > 1 ? args[1].Split(',').Select(int.Parse).ToArray() : new[] { 10_000, 100_000, 1_000_000 };
var results = new List<object>();
foreach (var size in sizes)
{
    var library = new BookLibrary(Path.Combine(output, "rows-" + size, "Data"));
    var books = await library.ListAsync(); var book = books.FirstOrDefault() ?? await library.CreateAsync("Benchmark", $"{size:N0} 条性能测试");
    var watch = Stopwatch.StartNew();
    using (var c = Database.Open(book.Path))
    {
        if (Convert.ToInt64(Database.Scalar(c, "SELECT COUNT(*) FROM Tasks")) == 0)
        {
            using var transaction = c.BeginTransaction();
            for (int i = 0; i < 10; i++) Database.Exec(c, "INSERT INTO Projects VALUES($id,$name)", ("$id", (i + 1).ToString("x32")), ("$name", "项目 " + i));
            var content = RichContent.FromText("完成今天的一件小事：整理资料、记录想法，并给自己一点休息时间。🌱");
            Database.Exec(c, """
                WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM seq WHERE x<$count)
                INSERT INTO Tasks
                SELECT printf('%032x',x),CASE WHEN x%11=0 THEN NULL ELSE printf('%032x',x%10+1) END,
                    $json,$text,x%3,$now-x*60000,$now-x*60000+500,
                    CASE WHEN x%3=2 THEN $now-x*60000+500 ELSE NULL END,1 FROM seq;
                """, ("$count", size), ("$json", content.ToJson()), ("$text", content.PlainText), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            transaction.Commit(); Database.Exec(c, "ANALYZE; PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }
    var seedMs = watch.Elapsed.TotalMilliseconds; var repo = new TaskRepository(book.Path);
    var queryResults = new List<object>(); var range = DateRanges.For(TaskFilter.Today, DateTime.Now);
    var shapes = new Dictionary<string, TaskQuery>
    {
        ["all-open"] = new(null, TaskFilter.Open, TaskSort.Created),
        ["project-open"] = new(1.ToString("x32"), TaskFilter.Open, TaskSort.Created),
        ["all-completed-date"] = new(null, TaskFilter.Completed, TaskSort.Completed),
        ["all-completed-created"] = new(null, TaskFilter.Completed, TaskSort.Created),
        ["project-completed-date"] = new(1.ToString("x32"), TaskFilter.Completed, TaskSort.Completed),
        ["project-completed-created"] = new(1.ToString("x32"), TaskFilter.Completed, TaskSort.Created),
        ["today"] = new(null, TaskFilter.Today, TaskSort.Created, range.From, range.Until),
        ["project-today"] = new(1.ToString("x32"), TaskFilter.Today, TaskSort.Created, range.From, range.Until),
        ["deep-cursor"] = new(null, TaskFilter.Open, TaskSort.Created, Cursor: new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - size * 50000L, size.ToString("x32")))
    };
    foreach (var (name, query) in shapes)
    {
        var samples = new List<double>(); int returned = 0;
        for (int i = 0; i < 50; i++)
        {
            watch.Restart(); var page = await repo.QueryAsync(query); watch.Stop(); returned = page.Items.Count;
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        using var c = Database.Open(book.Path); using var cmd = TaskRepository.BuildQuery(c, query, true); using var reader = cmd.ExecuteReader();
        var plan = new List<string>(); while (reader.Read()) plan.Add(reader.GetString(3));
        if (plan.Any(s => s.Contains("TEMP B-TREE"))) throw new InvalidOperationException(name + " requires a temporary sort");
        samples.Sort(); var p95 = samples[(int)Math.Ceiling(samples.Count * .95) - 1];
        queryResults.Add(new { Name = name, P95Milliseconds = Math.Round(p95, 3), Rows = returned, Plan = plan });
        Console.WriteLine($"{size,8:N0} {name,-26} p95={p95,7:F2} ms ({returned} rows)");
    }
    results.Add(new { TaskCount = size, SeedMilliseconds = seedMs, DatabaseBytes = new FileInfo(book.Path).Length, Queries = queryResults });
}
var report = new { Timestamp = DateTimeOffset.Now, Runtime = Environment.Version.ToString(), Environment.OSVersion, Environment.ProcessorCount, PageSize = 200, Results = results };
await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Report: " + Path.Combine(output, "results.json"));
