using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToDoList.Core;

public interface IRuntimeLog
{
    void Write(string category, string? operation, object? details, Exception? error);
}

public static class RuntimeLog
{
    public static IRuntimeLog? Writer { get; set; }
    private static readonly AsyncLocal<string?> Operation = new();
    public static string? OperationId => Operation.Value;
    public static void Write(string category, object? details = null, Exception? error = null)
    {
        try { Writer?.Write(category, Operation.Value, details, error); } catch { /* Diagnostics must not fail an operation. */ }
    }
    public static IDisposable Begin(string category, object? details = null) => new Scope(category, details);
    private sealed class Scope : IDisposable
    {
        private readonly string _category;
        private readonly string? _previous;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public Scope(string category, object? details)
        {
            _category = category; _previous = Operation.Value;
            Operation.Value ??= Guid.NewGuid().ToString("N"); Write(category + ".start", details);
        }
        public void Dispose() { Write(_category + ".end", new { elapsedMs = _watch.Elapsed.TotalMilliseconds }); Operation.Value = _previous; }
    }
}

public sealed class FileRuntimeLog : IRuntimeLog, IDisposable
{
    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly string _session = Guid.NewGuid().ToString("N")[..8];
    private int _part;
    private string? _date;
    private StreamWriter? _writer;
    private bool _failed, _disposed;
    public string DirectoryPath { get; }
    public event Action<Exception>? Failed;
    public FileRuntimeLog(string programDirectory, long maxBytes = 10 * 1024 * 1024)
    { DirectoryPath = Path.Combine(Path.GetFullPath(programDirectory), "Log"); _maxBytes = maxBytes; }

    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"https?://[^\s""'<>\\]+", "<url>", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"Bearer\s+[^\s""'<>]+", "Bearer <redacted>", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"((?:access_token|refresh_token|authorization|cookie|code_verifier|file_data)\s*[""']?\s*[:=]\s*[""']?)[^\s""',}]+", "$1<redacted>", RegexOptions.IgnoreCase);
        return text;
    }
    private static JsonNode? SafeDetails(object? details)
    {
        var node = JsonSerializer.SerializeToNode(details);
        void Clean(JsonNode? current)
        {
            if (current is JsonObject obj)
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    if (Regex.IsMatch(key, "token|cookie|authorization|file_data|check_data|upload_key|^ex$|contentJson|plainText|code_verifier|download_url", RegexOptions.IgnoreCase)) obj[key] = "<redacted>";
                    else if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s)) obj[key] = Redact(s);
                    else Clean(obj[key]);
                }
            else if (current is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var s)) array[i] = Redact(s); else Clean(array[i]);
        }
        Clean(node); return node;
    }
    public void Write(string category, string? operation, object? details, Exception? error)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var date = DateTime.Now.ToString("yyyyMMdd");
                if (_writer == null || _date != date || _writer.BaseStream.Length >= _maxBytes)
                {
                    _writer?.Dispose(); if (_date != date) { _date = date; _part = 0; }
                    var path = Path.Combine(DirectoryPath, $"run-{date}-{Environment.ProcessId}-{_session}-{_part++:D3}.jsonl");
                    _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
                    foreach (var old in Directory.EnumerateFiles(DirectoryPath, "run-*.jsonl"))
                        if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7))
                            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                _writer.WriteLine(JsonSerializer.Serialize(new { time = DateTimeOffset.Now, category, operation, details = SafeDetails(details), exception = error == null ? null : Redact(error.ToString()) }));
            }
            catch (Exception ex)
            {
                try { _writer?.Dispose(); } catch { } _writer = null;
                if (!_failed) { _failed = true; try { Failed?.Invoke(ex); } catch { } }
            }
        }
    }
    public void Dispose() { lock (_gate) { _disposed = true; try { _writer?.Dispose(); } catch { } _writer = null; } }
}
