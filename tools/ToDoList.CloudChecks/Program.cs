using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

if (args.Length == 2 && args[0] == "--diagnose") return await CloudRequestDiagnosis.RunAsync(args[1]);
if (args.Length == 2 && args[0] == "--db-cleanup") return await CloudDbLiveChecks.RunAsync(args[1]);
if (args.Length != 2) { Console.Error.WriteLine("Usage: ToDoList.CloudChecks <first-probe.dpapi> <second-probe.dpapi> | --db-cleanup <native-session.dpapi> | --diagnose <native-session.dpapi>"); return 2; }
var repo = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(repo, "ToDoList.sln"))) { Console.Error.WriteLine("Run from the ToDoList Git root."); return 2; }
var run = Path.Combine(repo, "artifacts", "cloud-live", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(run);
var results = new List<object>();
void Check(bool condition, string name)
{
    results.Add(new { name, passed = condition }); Console.WriteLine((condition ? "PASS: " : "FAIL: ") + name);
    if (!condition) throw new InvalidOperationException(name);
}
CloudSession ReadProbe(string path)
{
    var bytes = WindowsCloudCredentials.Unprotect(File.ReadAllBytes(path));
    try
    {
        using var json = JsonDocument.Parse(bytes); var root = json.RootElement; var tokens = root.GetProperty("tokens");
        return new(root.GetProperty("client_id").GetString()!, tokens.GetProperty("access_token").GetString()!, tokens.GetProperty("refresh_token").GetString()!,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + tokens.GetProperty("expires_in").GetInt64());
    }
    finally { Array.Clear(bytes); }
}
Task<bool> Apply(CloudBookStorage books, CloudSnapshot expected, CloudSnapshot desired, CancellationToken ct) => ApplyCore(books, expected, desired, ct);
static async Task<bool> ApplyCore(CloudBookStorage books, CloudSnapshot expected, CloudSnapshot desired, CancellationToken ct) { await books.ApplyAsync(expected, desired, ct); return true; }
try
{
    var first = ReadProbe(args[0]); var second = ReadProbe(args[1]); var spaceName = "CloudCheck-" + Guid.NewGuid().ToString("N");
    using var authA = new WeiyunAuthProvider(_ => throw new InvalidOperationException("Interactive login is not part of this test."), _ => { }, first);
    using var filesA = new WeiyunFileStore(authA, spaceName);
    var spaceA = await filesA.InitializeAsync();
    using var authB = new WeiyunAuthProvider(_ => { }, _ => { }, first);
    using var filesB = new WeiyunFileStore(authB, spaceName);
    var spaceB = await filesB.InitializeAsync(); Check(spaceA.Id == spaceB.Id && spaceA.DataKey == spaceB.DataKey && spaceA.SyncKey == spaceB.SyncKey, "Independent native clients reuse the same authorized directories");
    using var authOther = new WeiyunAuthProvider(_ => { }, _ => { }, second);
    using var filesOther = new WeiyunFileStore(authOther, spaceName);
    var otherSpace = await filesOther.InitializeAsync(); Check(otherSpace.Id != spaceA.Id, "Different WeChat accounts resolve to different native spaces");
    var large = RandomNumberGenerator.GetBytes(600000);
    var largeFile = await filesA.WriteImmutableAsync(CloudArea.Data, "native-hash-boundary.bin", large);
    Check((await filesA.ReadAsync(CloudArea.Data, largeFile)).SequenceEqual(large), "Native SHA1 intermediate states and multi-block upload match downloaded bytes");
    var repeated = await filesA.WriteImmutableAsync(CloudArea.Data, "native-hash-boundary.bin", large);
    Check(repeated.Id == largeFile.Id, "Repeated immutable upload reuses the existing object");
    try { await filesOther.ReadAsync(CloudArea.Data, largeFile); Check(false, "Cross-account reads are rejected"); }
    catch (CloudTransportException) { Check(true, "Cross-account reads are rejected"); }
    var libraryA = new BookLibrary(Path.Combine(run, "A", "Data")); var libraryB = new BookLibrary(Path.Combine(run, "B", "Data"));
    var bookA = await libraryA.CreateAsync("Native", "原生微云同步验证"); var repoA = new TaskRepository(bookA.Path);
    var project = await repoA.AddProjectAsync("验证模块");
    var task = await repoA.AddTaskAsync(new(1, [new([new("合成测试任务", Bold: true), new(" · HTTPS 链接", Link: "https://example.com")])]), project.Id);
    var done = await repoA.AddTaskAsync(RichContent.FromText("合成历史验证"), null); await repoA.SetStatusAsync(done.Id, TodoStatus.Completed, done.Revision);
    var storageA = new CloudBookStorage(libraryA); var storageB = new CloudBookStorage(libraryB);
    var a = new SyncCoordinator(filesA, storageA, spaceA.Id); var b = new SyncCoordinator(filesB, storageB, spaceB.Id);
    await a.SyncAsync(SyncDirection.Both, (x, y, ct) => Apply(storageA, x, y, ct));
    await b.SyncAsync(SyncDirection.Both, (x, y, ct) => Apply(storageB, x, y, ct));
    var bookB = (await libraryB.ListAsync()).Single(); var repoB = new TaskRepository(bookB.Path);
    var downloaded = (await repoB.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items;
    Check(downloaded.Any(t => t.Id == task.Id && t.ContentJson == task.ContentJson && t.ProjectId == project.Id), "Native coordinator downloads rich text and module identity");
    Check(downloaded.Any(t => t.Id == done.Id && t.Status == TodoStatus.Completed), "Native coordinator preserves status history and completion");
    await repoA.UpdateContentAsync(task.Id, RichContent.FromText("合成版本 A"), task.Revision);
    var taskB = downloaded.Single(t => t.Id == task.Id); await repoB.UpdateContentAsync(task.Id, RichContent.FromText("合成版本 B"), taskB.Revision);
    await a.SyncAsync(SyncDirection.Both, (x, y, ct) => Apply(storageA, x, y, ct));
    await b.SyncAsync(SyncDirection.Both, (x, y, ct) => Apply(storageB, x, y, ct));
    await a.SyncAsync(SyncDirection.Both, (x, y, ct) => Apply(storageA, x, y, ct));
    var conflict = a.Conflicts.Single(p => CloudSnapshot.Kind(p.Key) == "task"); Check(conflict.Value.Count == 2, "Real microcloud retains two incompatible task versions");
    await a.ResolveAsync(conflict.Key, 0, true, (x, y, ct) => Apply(storageA, x, y, ct));
    await a.SyncAsync(SyncDirection.Both, (x, y, ct) => Apply(storageA, x, y, ct));
    await b.SyncAsync(SyncDirection.Both, (x, y, ct) => Apply(storageB, x, y, ct));
    var resolved = (await repoB.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items;
    Check(resolved.Count(t => t.PlainText is "合成版本 A" or "合成版本 B") == 2 && b.Conflicts.Count == 0, "Keeping both creates separate tasks and converges through real microcloud");
    // Populate one application-owned directory past the official 50-item page size.
    var existing = await filesA.ListAsync(CloudArea.Sync); var target = 51 - existing.Count;
    for (var i = 0; i < target; i++) await filesA.WriteImmutableAsync(CloudArea.Sync, "pagination-" + i.ToString("D3") + ".txt", Encoding.UTF8.GetBytes("synthetic-pagination-" + i));
    var paged = await filesA.ListAsync(CloudArea.Sync); Check(paged.Count >= 51 && paged.Select(f => f.Id).Distinct().Count() == paged.Count, "Native directory pagination returns more than fifty files exactly once");
    // Seed an isolated WPF process; never activate a session for the user's installed app.
    var uiData = Path.Combine(run, "UI", "Data"); var credentials = new WindowsCloudCredentials(uiData);
    credentials.Save(authA.Session!); credentials.Activate(spaceA.Id);
    credentials.Save(authOther.Session!);
    CloudBookStorage.AtomicWrite(Path.Combine(run, "ui-input.json"), CloudJson.Serialize(new { dataDirectory = uiData, firstAccount = spaceA.Id, secondAccount = otherSpace.Id, testNamespace = spaceName }));
    CloudBookStorage.AtomicWrite(Path.Combine(run, "result.json"), CloudJson.Serialize(new { Completed = true, Passed = true, checks = results, remoteNamespace = "ToDoList/" + spaceName }));
    Console.WriteLine("REPORT=" + Path.Combine(run, "result.json")); Console.WriteLine("UI_INPUT=" + Path.Combine(run, "ui-input.json"));
    return 0;
}
catch (Exception ex)
{
    CloudBookStorage.AtomicWrite(Path.Combine(run, "result.json"), CloudJson.Serialize(new { Completed = true, Passed = false, checks = results, failure = ex.GetType().Name, message = ex is CloudTransportException or InvalidDataException or InvalidOperationException ? ex.Message : "Native cloud verification failed" }));
    Console.Error.WriteLine("FAILED=" + ex.GetType().Name); Console.WriteLine("REPORT=" + Path.Combine(run, "result.json")); return 1;
}
