using System.Text;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

internal static class CloudDbLiveChecks
{
    public static async Task<int> RunAsync(string credentialPath)
    {
        var root = Directory.GetCurrentDirectory();
        if (!File.Exists(Path.Combine(root, "ToDoList.sln"))) throw new InvalidOperationException("Run from project root.");
        var run = Path.Combine(root, "artifacts", "cloud-db-live", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        var checks = new List<string>(); var testNamespace = "CloudCheck-" + Guid.NewGuid().ToString("N"); string? failure = null;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); Console.WriteLine("PASS: " + name); }
        try
        {
            var plain = WindowsCloudCredentials.Unprotect(await File.ReadAllBytesAsync(credentialPath)); CloudSession session;
            try { session = CloudJson.Parse<CloudSession>(Encoding.UTF8.GetString(plain)); } finally { Array.Clear(plain); }
            var isolatedCredentials = new WindowsCloudCredentials(Path.Combine(run, "Credentials"));
            using var auth = new WeiyunAuthProvider(_ => throw new InvalidOperationException("No interactive login in this test."), isolatedCredentials.Save, session);
            using var files = new WeiyunFileStore(auth, testNamespace); var space = await files.InitializeAsync();
            var probe = CloudDbCodec.Encode(new() { AccountId = space.Id, DeviceId = Guid.NewGuid().ToString("N"), Generation = 1 });
            var file = await files.WriteImmutableAsync(CloudArea.Data, "delete-permission-probe.db", probe);
            var downloaded = await files.ReadAsync(CloudArea.Data, file);
            Check(downloaded.SequenceEqual(probe), "Synthetic DB upload/download is byte-identical");
            await files.RecycleAsync(CloudArea.Data, [file]);
            Check(!(await files.ListAsync(CloudArea.Data)).Any(v => v.Id == file.Id), "Actual delete permission verified: synthetic DB is absent after recycling");
            var aLibrary = new BookLibrary(Path.Combine(run, "A", "Data")); var aStorage = new CloudBookStorage(aLibrary);
            var bLibrary = new BookLibrary(Path.Combine(run, "B", "Data")); var bStorage = new CloudBookStorage(bLibrary);
            var a = new SyncCoordinator(files, aStorage, space.Id); var b = new SyncCoordinator(files, bStorage, space.Id);
            async Task<bool> Apply(CloudBookStorage storage, CloudSnapshot expected, CloudSnapshot desired, CancellationToken ct)
            { await storage.ApplyAsync(expected, desired, ct); return true; }
            async Task Sync(SyncCoordinator peer, CloudBookStorage storage)
            {
                var result = await peer.SyncAsync(SyncDirection.Both, (x, y, ct) => Apply(storage, x, y, ct));
                Check(!result.Deferred && result.CleanupWarning == null, "Sync and threshold cleanup completed");
            }
            var book = await aLibrary.CreateAsync("Synthetic", "DB 清理合成验证"); var repo = new TaskRepository(book.Path);
            var task = await repo.AddTaskAsync(RichContent.FromText("初始合成内容"), null);
            await Sync(a, aStorage); await Sync(b, bStorage);
            var bBook = (await bLibrary.ListAsync()).Single(); var bRepo = new TaskRepository(bBook.Path);
            var offline = await bRepo.AddTaskAsync(RichContent.FromText("清理历史后仍需保留的离线新增"), null);
            for (var i = 0; i < 5; i++)
            {
                task = await repo.UpdateContentAsync(task.Id, RichContent.FromText("连续同步 " + i), task.Revision); await Sync(a, aStorage);
                Check((await files.ListAsync(CloudArea.Data)).Count <= 2, "Cloud DB count stays at two despite repeated writes");
            }
            a = new(files, aStorage, space.Id); b = new(files, bStorage, space.Id);
            await Sync(b, bStorage); await Sync(a, aStorage);
            var final = (await repo.QueryAsync(new(null, TaskFilter.All, TaskSort.Created))).Items;
            Check(final.Any(v => v.Id == offline.Id) && final.Any(v => v.Id == task.Id && v.PlainText == task.PlainText), "Offline peer merges after old cloud generations were recycled");
            var data = await files.ListAsync(CloudArea.Data); var sync = await files.ListAsync(CloudArea.Sync);
            Check(data.All(v => CloudDbCodec.DeviceName.IsMatch(v.Name)) && sync.Count == 1 && CloudDbCodec.ProtocolName.IsMatch(sync[0].Name), "Only bounded DB files remain in the isolated cloud space");
            Check(data.GroupBy(v => CloudDbCodec.DeviceName.Match(v.Name).Groups[1].Value).All(g => g.Count() <= 2), "Each device retains no more than two DB files");
        }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; Console.WriteLine("FAILED: " + failure); }
        var report = Path.Combine(run, "result.json");
        CloudBookStorage.AtomicWrite(report, CloudJson.Serialize(new { Completed = true, Passed = failure == null, testNamespace, checks, failure }));
        Console.WriteLine("REPORT=" + report); return failure == null ? 0 : 1;
    }
}
