using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed class CloudAuthTests
{
    private static Dictionary<string, string> Query(string text) => text.TrimStart('?').Split('&').Where(s => s.Contains('=')).Select(s => s.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    [Fact] public async Task NativeLoopbackRejectsWrongStateAndUsesPkceWithoutClientSecret()
    {
        using var handler = new OAuthHandler(); using var http = new HttpClient(handler); Task? browser = null; var saved = new List<CloudSession>();
        using var auth = new WeiyunAuthProvider(url =>
        {
            var args = Query(new Uri(url).Query); handler.Challenge = args["code_challenge"];
            browser = Task.Run(async () =>
            {
                using var localhost = new HttpClient();
                var bad = await localhost.GetAsync(args["redirect_uri"] + "?code=fake-code&state=incorrect"); Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
                var good = await localhost.GetAsync(args["redirect_uri"] + "?code=fake-code&state=" + Uri.EscapeDataString(args["state"]));
                Assert.Equal(HttpStatusCode.OK, good.StatusCode); var html = await good.Content.ReadAsStringAsync();
                Assert.Contains("todolist-desktop://activate", html); Assert.DoesNotContain("fake-code", html);
                Assert.DoesNotContain(args["state"], html); Assert.Equal("text/html", good.Content.Headers.ContentType!.MediaType);
            });
        }, saved.Add, client: http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = await auth.LoginAsync(timeout.Token); await browser!;
        Assert.Equal("fake-access", session.AccessToken); Assert.Empty(saved);
        auth.AttachIdentity("stable-owned-space"); Assert.Single(saved);
        Assert.Equal("fake-refreshed", await auth.GetAccessTokenAsync(true)); Assert.Equal(2, saved.Count);
        Assert.Throws<IOException>(() => auth.AttachIdentity("different-space")); Assert.Null(http.DefaultRequestHeaders.Authorization);
    }
    [Fact] public async Task CancelledNativeLoginDoesNotSaveCredentials()
    {
        using var handler = new OAuthHandler(); using var http = new HttpClient(handler); using var cancelled = new CancellationTokenSource();
        using var auth = new WeiyunAuthProvider(_ => cancelled.Cancel(), _ => throw new InvalidOperationException("No credentials should be saved"), client: http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.LoginAsync(cancelled.Token)); Assert.Null(auth.Session);
    }
    [Fact] public async Task ForeignTokenEndpointIsRejectedBeforeOpeningBrowser()
    {
        using var handler = new OAuthHandler { ForeignEndpoint = true }; using var http = new HttpClient(handler);
        using var auth = new WeiyunAuthProvider(_ => throw new InvalidOperationException("Browser should not be opened"), _ => { }, client: http);
        await Assert.ThrowsAsync<CloudAuthenticationException>(() => auth.LoginAsync()); Assert.Equal(0, handler.RegisterCount);
    }
    [Fact] public async Task AsyncBrowserOpeningAndDeniedCallbackReleaseListener()
    {
        using var handler = new OAuthHandler(); using var http = new HttpClient(handler);
        string? redirect = null; Task? browser = null;
        using var auth = new WeiyunAuthProvider(async (url, ct) =>
        {
            await Task.Delay(30, ct); var args = Query(new Uri(url).Query); redirect = args["redirect_uri"];
            browser = Task.Run(async () =>
            {
                using var localhost = new HttpClient();
                var duplicate = await localhost.GetAsync(redirect + "?code=x&state=" + args["state"] + "&state=" + args["state"]);
                Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
                var missing = await localhost.GetAsync(redirect + "?state=" + args["state"]);
                Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
                var denied = await localhost.GetAsync(redirect + "?error=access_denied&state=" + args["state"]);
                Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
                Assert.DoesNotContain("todolist-desktop://activate", await denied.Content.ReadAsStringAsync());
            });
        }, _ => throw new InvalidOperationException("No credentials should be saved"), client: http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.LoginAsync(timeout.Token)); await browser!;
        Assert.Null(auth.Session);
        using var client = new System.Net.Sockets.TcpClient();
        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => client.ConnectAsync("127.0.0.1", new Uri(redirect!).Port));
    }
    [Fact] public async Task IsolatedCallbackCannotActivateAnotherInstalledApplication()
    {
        using var handler = new OAuthHandler(); using var http = new HttpClient(handler); Task? browser = null;
        using var auth = new WeiyunAuthProvider(url =>
        {
            var args = Query(new Uri(url).Query); handler.Challenge = args["code_challenge"];
            browser = Task.Run(async () =>
            {
                using var localhost = new HttpClient();
                var response = await localhost.GetAsync(args["redirect_uri"] + "?code=fake-code&state=" + args["state"]);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.DoesNotContain("todolist-desktop://activate", await response.Content.ReadAsStringAsync());
            });
        }, _ => { }, client: http, canActivateFromBrowser: () => false);
        await auth.LoginAsync(); await browser!;
        Assert.NotNull(auth.Session);
    }
    private sealed class OAuthHandler : HttpMessageHandler
    {
        public string? Challenge; public bool ForeignEndpoint; public int RegisterCount;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("www.weiyun.com", request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath.EndsWith("oauth-authorization-server")) return Json(new
            {
                issuer = "https://www.weiyun.com", authorization_endpoint = "https://www.weiyun.com/authorize/oauth",
                token_endpoint = ForeignEndpoint ? "https://foreign.invalid/token" : "https://www.weiyun.com/api/v3/mcpserver/oauth/token",
                registration_endpoint = "https://www.weiyun.com/api/v3/mcpserver/oauth/register",
                code_challenge_methods_supported = new[] { "S256" }, token_endpoint_auth_methods_supported = new[] { "none" }
            });
            if (request.RequestUri.AbsolutePath.EndsWith("/register"))
            {
                RegisterCount++; using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("none", data.RootElement.GetProperty("token_endpoint_auth_method").GetString());
                Assert.Equal("127.0.0.1", new Uri(data.RootElement.GetProperty("redirect_uris")[0].GetString()!).Host);
                return Json(new { client_id = "fake-client", token_endpoint_auth_method = "none" });
            }
            var values = Query(await request.Content!.ReadAsStringAsync(ct)); Assert.DoesNotContain("client_secret", values.Keys);
            if (values["grant_type"] == "authorization_code")
            {
                Assert.Equal("fake-code", values["code"]);
                Assert.Equal(Challenge, Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(values["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
                return Json(new { access_token = "fake-access", refresh_token = "fake-refresh", expires_in = 3600 });
            }
            Assert.Equal("fake-refresh", values["refresh_token"]);
            return Json(new { access_token = "fake-refreshed", refresh_token = "fake-rotated-refresh", expires_in = 3600 });
        }
        private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    }
}
