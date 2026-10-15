using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ToDoList.Core;

namespace ToDoList.Storage.Cloud;

public enum CloudFailureKind { Transport, Protocol, RateLimit, DailyQuota, Disabled, StorageQuota }
public sealed class CloudTransportException(string message, HttpStatusCode? status = null, CloudFailureKind kind = CloudFailureKind.Transport, int? code = null, DateTimeOffset? retryAt = null, Exception? inner = null) : IOException(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
    public CloudFailureKind Kind { get; } = kind;
    public int? Code { get; } = code;
    public DateTimeOffset? RetryAt { get; internal set; } = retryAt;
}
public sealed class WeiyunFileStore(ICloudAuthProvider auth, string? testNamespace = null, HttpClient? client = null) : ICloudFileStore, IDisposable
{
    public const int MaxObjectBytes = 32 * 1024 * 1024;
    private readonly HttpClient _http = client ?? new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(2) };
    private CloudSpace? _space;
    private long _requestId;
    private async Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken ct)
    {
        var forceRefresh = false;
        for (var attempt = 0; ; attempt++)
        {
            var watch = Stopwatch.StartNew(); var requestId = Interlocked.Increment(ref _requestId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2)); var requestToken = timeout.Token;
            RuntimeLog.Write("cloud.request", new { tool, requestId, attempt, arguments });
            try
            {
            using var request = new HttpRequestMessage(HttpMethod.Post, WeiyunAuthProvider.McpEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetAccessTokenAsync(forceRefresh, requestToken)); forceRefresh = false;
            request.Headers.Accept.ParseAdd("application/json"); request.Headers.Accept.ParseAdd("text/event-stream");
            request.Content = JsonContent.Create(new { jsonrpc = "2.0", id = requestId, method = "tools/call", @params = new { name = tool, arguments } });
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            var bytes = await ReadMcpBodyAsync(response, requestId, requestToken);
            var body = Encoding.UTF8.GetString(bytes);
            RuntimeLog.Write("cloud.response", new { tool, requestId, status = (int)response.StatusCode, elapsedMs = watch.Elapsed.TotalMilliseconds, retryAfter = response.Headers.RetryAfter?.ToString(), contentType = response.Content.Headers.ContentType?.MediaType, bytes = bytes.Length });
            var retryAt = response.Headers.RetryAfter?.Date ?? (response.Headers.RetryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            { RuntimeLog.Write("cloud.rate-limit", new { tool, requestId, response = body }); var failure = Failure(body, tool, response.StatusCode, retryAt); if (failure.Kind is CloudFailureKind.DailyQuota or CloudFailureKind.Disabled) throw failure; throw new CloudTransportException("微云请求过于频繁，已暂停请求，等待冷却后再同步。", response.StatusCode, CloudFailureKind.RateLimit, retryAt: retryAt); }
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { forceRefresh = true; continue; }
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new CloudAuthenticationException();
            if ((int)response.StatusCode >= 500 && attempt < 3)
            {
                RuntimeLog.Write("cloud.server-error", new { tool, requestId, response = body });
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(1 << attempt);
                RuntimeLog.Write("cloud.retry", new { tool, attempt, delayMs = delay.TotalMilliseconds });
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), requestToken); continue;
            }
            if (!response.IsSuccessStatusCode)
            { RuntimeLog.Write("cloud.http-error", new { tool, requestId, response = body }); throw Failure(body, tool, response.StatusCode, retryAt); }
            JsonElement root;
            try { root = ParseEnvelope(body, response.Content.Headers.ContentType?.MediaType, requestId); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            { RuntimeLog.Write("cloud.parse-error", new { tool, requestId, response = body }, ex); throw new CloudTransportException("微云响应格式无效（" + tool + "）。", kind: CloudFailureKind.Protocol, inner: ex); }
            if (root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out var result) || result.TryGetProperty("isError", out var err) && err.GetBoolean())
            { RuntimeLog.Write("cloud.protocol-error", new { tool, requestId, response = root }); throw Failure(root.ToString(), tool, response.StatusCode, retryAt); }
            JsonElement data;
            try
            {
                if (result.TryGetProperty("structuredContent", out var structured)) data = structured.Clone();
                else { using var textDoc = JsonDocument.Parse(result.GetProperty("content").EnumerateArray().First(v => v.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!); data = textDoc.RootElement.Clone(); }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            { RuntimeLog.Write("cloud.result-error", new { tool, requestId, response = result }, ex); throw Failure(result.ToString(), tool, response.StatusCode, retryAt); }
            if (data.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False) && !string.IsNullOrEmpty(error.ToString()))
            { RuntimeLog.Write("cloud.business-error", new { tool, requestId, response = data }); throw Failure(data.ToString(), tool, response.StatusCode, retryAt); }
            return data.Clone();
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            { RuntimeLog.Write("cloud.timeout", new { tool, requestId, elapsedMs = watch.Elapsed.TotalMilliseconds }, ex); throw new CloudTransportException("微云请求超时（" + tool + "），本地数据已保留。", inner: ex); }
            catch (Exception ex) { RuntimeLog.Write("cloud.request.failed", new { tool, requestId, elapsedMs = watch.Elapsed.TotalMilliseconds }, ex); throw; }
        }
    }
    public static CloudTransportException Failure(string text, string tool, HttpStatusCode? status = null, DateTimeOffset? retryAt = null)
    {
        if (Regex.IsMatch(text, @"(?<!\d)117401(?!\d)")) return new("微云当日调用配额已耗尽，暂停请求，等待次日配额恢复；本地数据已保留。", status, CloudFailureKind.DailyQuota, 117401, retryAt);
        if (Regex.IsMatch(text, @"(?<!\d)117407(?!\d)")) return new("微云授权凭证已被禁用，请重新授权；若为安全限制，请联系微云客服。", status, CloudFailureKind.Disabled, 117407);
        if (text.Contains("-6014") || text.Contains("空间不足")) return new("微云空间不足，请清理或扩容后重试；本地数据已保留。", status, CloudFailureKind.StorageQuota, -6014);
        if (Regex.IsMatch(text, @"(?<!\d)117402(?!\d)")) return new("微云授权凭证无效，请重新登录。", status, code: 117402);
        return new("微云未能完成请求（" + tool + (status == null ? "" : "，HTTP " + (int)status) + "），详情已记录到运行日志。", status, CloudFailureKind.Protocol);
    }
    public static JsonElement ParseEnvelope(string body, string? contentType, long requestId)
    {
        if (contentType != "text/event-stream") { using var doc = JsonDocument.Parse(body); return doc.RootElement.Clone(); }
        foreach (var block in body.Replace("\r\n", "\n").Split("\n\n"))
        {
            var data = string.Join("\n", block.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].TrimStart(' ')));
            if (data.Length == 0 || data == "[DONE]") continue;
            using var doc = JsonDocument.Parse(data); var root = doc.RootElement;
            if (root.TryGetProperty("id", out var id) && (id.ToString() == requestId.ToString(System.Globalization.CultureInfo.InvariantCulture))) return root.Clone();
        }
        throw new InvalidDataException("微云事件流未返回当前请求的结果。");
    }
    private static async Task<byte[]> ReadMcpBodyAsync(HttpResponseMessage response, long requestId, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "text/event-stream") return await ReadBoundedAsync(stream, 64 * 1024 * 1024, ct);
        // An SSE connection can remain open after its result; do not wait for EOF.
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var body = new StringBuilder(); var block = new StringBuilder(); var byteCount = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            byteCount += Encoding.UTF8.GetByteCount(line) + 1;
            if (byteCount > 64 * 1024 * 1024) throw new InvalidDataException("微云事件流过大。");
            body.AppendLine(line); block.Append(line).Append('\n');
            if (line.Length != 0) continue;
            try { ParseEnvelope(block.ToString(), "text/event-stream", requestId); return Encoding.UTF8.GetBytes(body.ToString()); }
            catch (InvalidDataException) { block.Clear(); }
            catch (JsonException ex) { RuntimeLog.Write("cloud.sse.invalid", new { response = body.ToString() }, ex); throw; }
        }
        return Encoding.UTF8.GetBytes(body.ToString());
    }
    private async Task<(string Key, List<(string Name, string Key)> Dirs, List<CloudFile> Files)> DirectoryAsync(string? key, string? parent, CancellationToken ct, int getType)
    {
        var dirs = new List<(string, string)>(); var files = new List<CloudFile>(); var offset = 0; string? current = null;
        while (true)
        {
            var args = new Dictionary<string, object> { ["limit"] = 50, ["offset"] = offset, ["get_type"] = getType, ["order_by"] = 1, ["asc"] = true };
            if (key != null) args["dir_key"] = key; if (parent != null) args["pdir_key"] = parent;
            var page = await CallAsync("weiyun.list", args, ct);
            var pageKey = page.GetProperty("pdir_key").GetString();
            if (string.IsNullOrWhiteSpace(pageKey) || current != null && pageKey != current) throw new CloudTransportException("微云目录身份发生变化，已暂停同步。");
            current = pageKey; var received = 0;
            foreach (var dir in page.GetProperty("dir_list").EnumerateArray()) { dirs.Add((dir.GetProperty("dir_name").GetString()!, dir.GetProperty("dir_key").GetString()!)); received++; }
            foreach (var file in page.GetProperty("file_list").EnumerateArray()) { files.Add(new(file.GetProperty("file_id").GetString()!, file.GetProperty("filename").GetString()!, file.GetProperty("file_size").GetInt64())); received++; }
            if (page.GetProperty("finish_flag").GetBoolean()) return (current, dirs, files);
            if (received == 0 || offset > 200000) throw new CloudTransportException("微云目录分页异常，已暂停同步。");
            offset += received;
        }
    }
    private async Task<string> EnsureDirectoryAsync(string name, string parent, string parentParent, CancellationToken ct)
    {
        var listing = await DirectoryAsync(parent, parentParent, ct, 1);
        var matching = listing.Dirs.Where(d => d.Name == name).ToArray();
        if (matching.Length > 1) throw new CloudTransportException("微云存在多份同名目录，请整理后重试：" + name);
        if (matching.Length == 1) return matching[0].Key;
        var created = await CallAsync("weiyun.create_dir", new { dir_name = name, pdir_key = listing.Key }, ct);
        if (created.GetProperty("dir_name").GetString() != name) throw new CloudTransportException("微云自动修改了目录名称，已暂停同步。请重新登录后重试。");
        return created.GetProperty("dir_key").GetString() ?? throw new CloudTransportException("微云目录创建结果不完整。");
    }
    public async Task<CloudSpace> InitializeAsync(CancellationToken ct = default)
    {
        // Keys are bound to this authenticated connection; each list still verifies identity.
        if (_space != null && auth.Session?.AccountId == _space.Id) return _space;
        var root = await DirectoryAsync(null, null, ct, 1); auth.AttachIdentity(root.Key);
        var apps = root.Dirs.Where(d => d.Name == "ToDoList").ToArray();
        if (apps.Length > 1) throw new CloudTransportException("微云存在多份 ToDoList 目录，请整理后重试。");
        string app;
        if (apps.Length == 1) app = apps[0].Key;
        else
        {
            var created = await CallAsync("weiyun.create_dir", new { dir_name = "ToDoList", pdir_key = root.Key }, ct);
            if (created.GetProperty("dir_name").GetString() != "ToDoList") throw new CloudTransportException("微云父目录名称发生冲突，请重试。");
            app = created.GetProperty("dir_key").GetString()!;
        }
        var appParent = root.Key;
        if (testNamespace != null)
        {
            if (!Regex.IsMatch(testNamespace, "\\ACloudCheck-[a-f0-9]{32}\\z")) throw new ArgumentException("无效的隔离测试目录。");
            var test = await EnsureDirectoryAsync(testNamespace, app, root.Key, ct); appParent = app; app = test;
        }
        var children = await DirectoryAsync(app, appParent, ct, 1);
        async Task<string> Child(string name)
        {
            var matches = children.Dirs.Where(d => d.Name == name).ToArray();
            if (matches.Length > 1) throw new CloudTransportException("微云存在多份同名目录：" + name);
            if (matches.Length == 1) return matches[0].Key;
            var created = await CallAsync("weiyun.create_dir", new { dir_name = name, pdir_key = children.Key }, ct);
            if (created.GetProperty("dir_name").GetString() != name) throw new CloudTransportException("微云目录创建结果名称不一致。");
            return created.GetProperty("dir_key").GetString()!;
        }
        var data = await Child("Data"); var sync = await Child("Sync");
        _appKey = app; _space = new(auth.Session!.AccountId, root.Key, data, sync); return _space;
    }
    private string? _appKey;
    public async Task CheckAccessAsync(CancellationToken ct = default)
    {
        // Diagnostic path: one directory listing, never create or write cloud files.
        var root = await DirectoryAsync(null, null, ct, 1); auth.AttachIdentity(root.Key);
    }
    private string AreaKey(CloudArea area) => _space == null ? throw new InvalidOperationException("尚未连接微云目录。") : area == CloudArea.Data ? _space.DataKey : _space.SyncKey;
    public async Task<IReadOnlyList<CloudFile>> ListAsync(CloudArea area, CancellationToken ct = default)
    {
        var key = AreaKey(area); var listing = await DirectoryAsync(key, _appKey, ct, 2);
        if (listing.Key != key) throw new CloudTransportException("微云目录身份发生变化，已暂停同步。");
        return listing.Files;
    }
    public async Task RecycleAsync(CloudArea area, IReadOnlyList<CloudFile> files, CancellationToken ct = default)
    {
        if (files.Count == 0) return;
        var listed = await ListAsync(area, ct);
        var selected = files.Select(f => listed.SingleOrDefault(v => v.Id == f.Id && v.Name == f.Name && v.Size == f.Size)
            ?? throw new InvalidDataException("待回收云文件身份已变化，未删除文件。")).ToArray();
        foreach (var batch in selected.Chunk(50))
        {
            await CallAsync("weiyun.delete", new { delete_completely = false,
                file_list = batch.Select(f => new { file_id = f.Id, pdir_key = AreaKey(area) }).ToArray() }, ct);
        }
        var remaining = await ListAsync(area, ct);
        if (selected.Any(f => remaining.Any(v => v.Id == f.Id))) throw new CloudTransportException("微云未能确认文件已移入回收站，将稍后重试。");
    }
    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream(); var block = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(block, ct)) > 0) { if (buffer.Length + count > limit) throw new InvalidDataException("云存档对象过大，已暂停同步。"); await buffer.WriteAsync(block.AsMemory(0, count), ct); }
        return buffer.ToArray();
    }
    public async Task<byte[]> ReadAsync(CloudArea area, CloudFile file, CancellationToken ct = default)
    {
        using var operation = RuntimeLog.Begin("cloud.download", new { area, file.Name, file.Size });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
        if (file.Size is < 0 or > MaxObjectBytes) throw new InvalidDataException("云存档对象大小不受支持。");
        var allowed = (await ListAsync(area, ct)).SingleOrDefault(f => f.Id == file.Id && f.Name == file.Name);
        if (allowed == null) throw new CloudTransportException("云存档已变化，请重新同步。");
        var links = await CallAsync("weiyun.download", new { items = new[] { new { file_id = file.Id, pdir_key = AreaKey(area) } } }, ct);
        var items = links.GetProperty("items").EnumerateArray().ToArray();
        if (items.Length != 1) throw new CloudTransportException("微云未返回存档下载地址。");
        var uri = new Uri(items[0].GetProperty("https_download_url").GetString()!);
        if (uri.Scheme != "https") throw new CloudTransportException("微云返回了不安全的下载地址。");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var cookie = items[0].GetProperty("cookie").GetString(); if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        // The HTTP client has no default bearer/cookies and does not follow redirects.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        RuntimeLog.Write("cloud.download.response", new { status = (int)response.StatusCode, file.Size });
        if (!response.IsSuccessStatusCode) throw new CloudTransportException("微云存档下载失败，请重新同步。", response.StatusCode);
        var bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(timeout.Token), MaxObjectBytes, timeout.Token);
        if (bytes.Length != allowed.Size) throw new InvalidDataException("下载存档大小不一致。"); return bytes;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new CloudTransportException("微云存档下载超时，本地数据已保留。", inner: ex); }
    }
    public async Task<CloudFile> WriteImmutableAsync(CloudArea area, string name, byte[] bytes, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(name, "\\A[a-zA-Z0-9_.-]{1,160}\\z") || bytes.Length is < 1 or > MaxObjectBytes) throw new ArgumentException("云存档对象名称或大小无效。");
        var matches = (await ListAsync(area, ct)).Where(f => f.Name == name).ToArray();
        if (matches.Length > 1) throw new CloudTransportException("云端存在同名存档冲突。");
        if (matches.Length == 1)
        {
            if (CloudJson.Hash(await ReadAsync(area, matches[0], ct)) != CloudJson.Hash(bytes)) throw new InvalidDataException("同名云存档的校验值不同，未覆盖该对象。");
            return matches[0];
        }
        var hash = await Task.Run(() => WeiyunHash.Compute(bytes), ct);
        var pre = new { filename = name, file_size = bytes.Length, file_sha = hash.FileSha, file_md5 = hash.FileMd5,
            block_sha_list = hash.BlockShaList, check_sha = hash.CheckSha, check_data = hash.CheckData, pdir_key = AreaKey(area) };
        for (var round = 0; round < Math.Max(50, bytes.Length / 65536 + 10); round++)
        {
            ct.ThrowIfCancellationRequested(); var result = await CallAsync("weiyun.upload", pre, ct);
            if (result.GetProperty("file_exist").GetBoolean() || result.GetProperty("upload_state").GetInt32() == 2) break;
            var channels = result.GetProperty("channel_list").Clone();
            var channel = channels.EnumerateArray().FirstOrDefault(c => c.GetProperty("len").GetInt64() > 0);
            if (channel.ValueKind == JsonValueKind.Undefined) throw new CloudTransportException("微云未提供可用的上传通道。");
            var offset = channel.GetProperty("offset").GetInt64(); var length = channel.GetProperty("len").GetInt64();
            if (offset < 0 || offset >= bytes.Length || length <= 0) throw new CloudTransportException("微云上传通道范围无效。");
            var chunk = Convert.ToBase64String(bytes, (int)offset, (int)Math.Min(length, bytes.Length - offset));
            var uploaded = await CallAsync("weiyun.upload", new { filename = name, file_size = bytes.Length, file_sha = hash.FileSha, block_sha_list = Array.Empty<string>(),
                check_sha = hash.CheckSha, upload_key = result.GetProperty("upload_key").GetString(), channel_list = channels,
                channel_id = channel.GetProperty("id").GetInt32(), ex = result.GetProperty("ex").GetString(), file_data = chunk, pdir_key = AreaKey(area) }, ct);
            if (uploaded.GetProperty("upload_state").GetInt32() == 2) break;
            if (round == Math.Max(50, bytes.Length / 65536 + 10) - 1) throw new CloudTransportException("微云上传未能完成，将稍后重试。");
        }
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var completed = (await ListAsync(area, ct)).Where(f => f.Name == name).ToArray();
            if (completed.Length > 1) throw new CloudTransportException("云端上传产生同名冲突。");
            if (completed.Length == 1)
            {
                if (CloudJson.Hash(await ReadAsync(area, completed[0], ct)) != CloudJson.Hash(bytes)) throw new InvalidDataException("云端上传校验失败。");
                return completed[0];
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct);
        }
        throw new CloudTransportException("上传完成后暂未能确认存档，将稍后重试。");
    }
    public void Dispose() { if (client == null) _http.Dispose(); }
}
