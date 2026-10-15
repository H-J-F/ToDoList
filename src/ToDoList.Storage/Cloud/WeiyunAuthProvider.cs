using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToDoList.Core;

namespace ToDoList.Storage.Cloud;

public sealed record CloudSession(string ClientId, string AccessToken, string RefreshToken, long ExpiresAt, string? RootKey = null, long? AuthorizedAt = null)
{
    public bool IsAuthorizationValid(DateTimeOffset now) => AuthorizedAt is long at && at > 0 && at <= now.ToUnixTimeSeconds() && at > now.ToUnixTimeSeconds() - 14L * 86400;
    public string AccountId => RootKey == null ? throw new InvalidOperationException("尚未确认云空间身份。") : CloudJson.Hash(Encoding.UTF8.GetBytes("https://www.weiyun.com|" + RootKey));
}
public sealed class CloudAuthenticationException() : IOException("微云授权已失效，请重新登录；本地数据仍然保留。");
public interface ICloudAuthProvider
{
    CloudSession? Session { get; }
    Task<CloudSession> LoginAsync(CancellationToken ct = default);
    Task<string> GetAccessTokenAsync(bool refresh = false, CancellationToken ct = default);
    void AttachIdentity(string rootKey);
}
public sealed class WeiyunAuthProvider(Func<string, CancellationToken, Task> openBrowser, Action<CloudSession> save, CloudSession? restored = null, HttpClient? client = null,
    Func<bool>? canActivateFromBrowser = null, TimeProvider? timeProvider = null) : ICloudAuthProvider, IDisposable
{
    public WeiyunAuthProvider(Action<string> openBrowser, Action<CloudSession> save, CloudSession? restored = null, HttpClient? client = null,
        Func<bool>? canActivateFromBrowser = null, TimeProvider? timeProvider = null)
        : this((url, ct) => { ct.ThrowIfCancellationRequested(); openBrowser(url); return Task.CompletedTask; }, save, restored, client, canActivateFromBrowser, timeProvider) { }
    private const string Host = "https://www.weiyun.com";
    private const string TokenEndpoint = Host + "/api/v3/mcpserver/oauth/token";
    public const string McpEndpoint = Host + "/api/v3/mcpserver";
    private readonly HttpClient _http = client ?? new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly SemaphoreSlim _gate = new(1, 1);
    public CloudSession? Session { get; private set; } = restored;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private async Task<JsonElement> PostAsync(string uri, HttpContent content, CancellationToken ct)
    {
        using var operation = RuntimeLog.Begin("cloud.oauth", new { endpoint = new Uri(uri).AbsolutePath });
        using var response = await _http.PostAsync(uri, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            RuntimeLog.Write("cloud.oauth.failed", new { status = (int)response.StatusCode, response = body }); throw new CloudAuthenticationException();
        }
        RuntimeLog.Write("cloud.oauth.completed", new { status = (int)response.StatusCode });
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.Clone();
    }
    private CloudSession ReadTokens(JsonElement token, string clientId, string? rootKey, string? oldRefresh = null, long? authorizedAt = null)
    {
        if (!token.TryGetProperty("access_token", out var access) || string.IsNullOrWhiteSpace(access.GetString())) throw new CloudAuthenticationException();
        var refresh = token.TryGetProperty("refresh_token", out var r) ? r.GetString() : oldRefresh;
        if (string.IsNullOrWhiteSpace(refresh)) throw new CloudAuthenticationException();
        var lifetime = token.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var seconds) && seconds > 0 ? seconds : 3600;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        return new(clientId, access.GetString()!, refresh, now + Math.Min(lifetime, 366L * 86400), rootKey, authorizedAt ?? now);
    }
    public async Task<CloudSession> LoginAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var token = timeout.Token;
            using var metadataResponse = await _http.GetAsync(Host + "/.well-known/oauth-authorization-server", token);
            metadataResponse.EnsureSuccessStatusCode();
            var metadata = CloudJson.Parse<JsonElement>(await metadataResponse.Content.ReadAsStringAsync(token));
            if (metadata.GetProperty("issuer").GetString() != Host || metadata.GetProperty("token_endpoint").GetString() != TokenEndpoint ||
                metadata.GetProperty("authorization_endpoint").GetString() != Host + "/authorize/oauth" ||
                metadata.GetProperty("registration_endpoint").GetString() != Host + "/api/v3/mcpserver/oauth/register" ||
                !metadata.GetProperty("code_challenge_methods_supported").EnumerateArray().Any(v => v.GetString() == "S256") ||
                !metadata.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Any(v => v.GetString() == "none")) throw new CloudAuthenticationException();
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://127.0.0.1:{port}/callback";
            using var registrationContent = JsonContent.Create(new { client_name = "ToDoList", redirect_uris = new[] { redirect },
                grant_types = new[] { "authorization_code", "refresh_token" }, response_types = new[] { "code" },
                token_endpoint_auth_method = "none", scope = "files:read files:write files:manage" });
            var registration = await PostAsync(Host + "/api/v3/mcpserver/oauth/register", registrationContent, token);
            var clientId = registration.GetProperty("client_id").GetString() ?? throw new CloudAuthenticationException();
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32)); var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var query = new Dictionary<string, string> { ["client_id"] = clientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
                ["scope"] = "files:read files:write files:manage", ["state"] = state, ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                ["code_challenge_method"] = "S256", ["resource"] = McpEndpoint };
            await openBrowser(Host + "/authorize/oauth?" + string.Join('&', query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))), token);
            string? code = null;
            while (code == null)
            {
                using var connection = await listener.AcceptTcpClientAsync(token);
                using var stream = connection.GetStream();
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var header = new byte[8192]; var count = 0;
                try
                {
                    while (count < header.Length && await stream.ReadAsync(header.AsMemory(count, 1), readTimeout.Token) > 0)
                    {
                        count++;
                        if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10) break;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                var lines = Encoding.ASCII.GetString(header, 0, count).Split("\r\n"); var requestLine = lines[0].Split(' ');
                var queryValues = new Dictionary<string, string>(StringComparer.Ordinal); var queryValid = true;
                Uri? callback = null;
                var valid = count < header.Length && requestLine.Length == 3 && requestLine[0] == "GET" && requestLine[1].StartsWith("/callback", StringComparison.Ordinal) &&
                    Uri.TryCreate("http://127.0.0.1:" + port + requestLine[1], UriKind.Absolute, out callback) && callback.AbsolutePath == "/callback" &&
                    lines.Any(l => l.Equals("Host: 127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase));
                if (valid)
                    foreach (var pair in callback!.Query.TrimStart('?').Split('&'))
                    {
                        var parts = pair.Split('=', 2);
                        if (parts.Length != 2 || !queryValues.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1].Replace('+', ' ')))) queryValid = false;
                    }
                valid = valid && queryValid && queryValues.GetValueOrDefault("state") == state &&
                    (queryValues.ContainsKey("error") || !string.IsNullOrWhiteSpace(queryValues.GetValueOrDefault("code")));
                var received = valid && !queryValues.ContainsKey("error");
                var message = received ? "授权已收到，ToDoList 正在连接微云。" : valid ? "本次授权已取消。" : "授权回调无效。";
                var canActivate = received && (canActivateFromBrowser?.Invoke() ?? true);
                var html = "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\"><title>ToDoList</title><body><h1>" + message + "</h1>" +
                    (received ? "<p>内嵌登录将在完成后自动关闭。系统浏览器登录会尝试恢复 ToDoList 窗口。</p>" : "") +
                    (canActivate ? "<a href=\"todolist-desktop://activate\">打开 ToDoList</a>" : "") + "</body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                var responseHeader = Encoding.ASCII.GetBytes("HTTP/1.1 " + (valid ? "200 OK" : "400 Bad Request") + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Security-Policy: default-src 'none'; base-uri 'none'; frame-ancestors 'none'\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + bytes.Length + "\r\n\r\n");
                await stream.WriteAsync(responseHeader, token); await stream.WriteAsync(bytes, token);
                if (!valid) continue;
                if (queryValues.ContainsKey("error")) throw new OperationCanceledException("微云授权已取消。", ct);
                code = queryValues.GetValueOrDefault("code");
            }
            using var exchange = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = clientId,
                ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier, ["resource"] = McpEndpoint });
            Session = ReadTokens(await PostAsync(TokenEndpoint, exchange, token), clientId, null);
            // Persist only after the file store has verified the space identity.
            return Session;
            }
            finally { listener.Stop(); }
        }
        finally { _gate.Release(); }
    }
    public void AttachIdentity(string rootKey)
    {
        if (Session == null || string.IsNullOrWhiteSpace(rootKey)) throw new CloudAuthenticationException();
        if (Session.RootKey != null && Session.RootKey != rootKey) throw new IOException("授权空间身份发生变化，已停止同步。请重新登录。");
        Session = Session with { RootKey = rootKey }; save(Session);
    }
    public async Task<string> GetAccessTokenAsync(bool refresh = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var session = Session ?? throw new CloudAuthenticationException();
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            if (session.AuthorizedAt == null)
            {
                Session = session = session with { AuthorizedAt = now.ToUnixTimeSeconds() };
                if (session.RootKey != null) save(session);
            }
            if (!session.IsAuthorizationValid(now)) throw new CloudAuthenticationException();
            if (!refresh && session.ExpiresAt > now.ToUnixTimeSeconds() + 120) return session.AccessToken;
            using var request = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = session.ClientId, ["refresh_token"] = session.RefreshToken });
            Session = ReadTokens(await PostAsync(TokenEndpoint, request, ct), session.ClientId, session.RootKey, session.RefreshToken, session.AuthorizedAt);
            if (Session.RootKey != null) save(Session); return Session.AccessToken;
        }
        finally { _gate.Release(); }
    }
    public void Dispose() { if (client == null) _http.Dispose(); }
}
