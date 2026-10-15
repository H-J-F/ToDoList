using System.Diagnostics;
using System.Text;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

internal static class CloudRequestDiagnosis
{
    public static async Task<int> RunAsync(string path)
    {
        var run = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "sync-diagnosis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        using var log = new FileRuntimeLog(run); RuntimeLog.Writer = log;
        using var operation = RuntimeLog.Begin("diagnosis.read-only");
        var watch = Stopwatch.StartNew();
        try
        {
            var plain = WindowsCloudCredentials.Unprotect(await File.ReadAllBytesAsync(path)); CloudSession session;
            try { session = CloudJson.Parse<CloudSession>(Encoding.UTF8.GetString(plain)); } finally { Array.Clear(plain); }
            using var files = new WeiyunFileStore(new ReadOnlyAuth(session));
            await files.CheckAccessAsync();
            CloudBookStorage.AtomicWrite(Path.Combine(run, "result.json"), CloudJson.Serialize(new { completed = true, passed = true, elapsedSeconds = watch.Elapsed.TotalSeconds, request = "weiyun.list", writes = 0 }));
            Console.WriteLine("Read-only directory request succeeded; seconds=" + watch.Elapsed.TotalSeconds.ToString("F2")); return 0;
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("diagnosis.failed", error: ex);
            CloudBookStorage.AtomicWrite(Path.Combine(run, "result.json"), CloudJson.Serialize(new { completed = true, passed = false, elapsedSeconds = watch.Elapsed.TotalSeconds,
                kind = (ex as CloudTransportException)?.Kind.ToString(), code = (ex as CloudTransportException)?.Code, status = (ex as CloudTransportException)?.Status,
                error = RuntimeLog.OperationId + ": " + FileRuntimeLog.Redact(ex.Message), writes = 0 }));
            Console.WriteLine(FileRuntimeLog.Redact(ex.Message)); return 1;
        }
        finally { Console.WriteLine("REPORT=" + Path.Combine(run, "result.json")); }
    }
    private sealed class ReadOnlyAuth(CloudSession session) : ICloudAuthProvider
    {
        public CloudSession? Session => session;
        public Task<CloudSession> LoginAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetAccessTokenAsync(bool refresh = false, CancellationToken ct = default)
        {
            if (refresh) throw new CloudAuthenticationException(); // Never rotate the running instance's credential.
            return Task.FromResult(session.AccessToken);
        }
        public void AttachIdentity(string rootKey) { if (rootKey != session.RootKey) throw new InvalidDataException("Diagnostic account identity mismatch."); }
    }
}
