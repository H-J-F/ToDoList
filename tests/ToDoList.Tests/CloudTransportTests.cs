using System.Net;
using System.Text;
using System.Text.Json;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed class CloudTransportTests
{
    [Theory] [InlineData(117401, CloudFailureKind.DailyQuota)] [InlineData(117407, CloudFailureKind.Disabled)] [InlineData(-6014, CloudFailureKind.StorageQuota)]
    public void BusinessCodesHaveDistinctCategories(int code, CloudFailureKind expected)
    { var error = WeiyunFileStore.Failure("type:business, code:" + code, "weiyun.list"); Assert.Equal(expected, error.Kind); Assert.Equal(code, error.Code); }

    [Fact] public async Task ActualQuotaEnvelopeIsLoggedAsDailyQuotaWithoutRetry()
    {
        var handler = new Replies(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32603,\"message\":\"tool execution failed: type:business, code:117401, msg:rate limit exceeded, daily quota used up. Please try again tomorrow\"}}", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler); using var files = new WeiyunFileStore(new Auth(), client: http);
        var error = await Assert.ThrowsAsync<CloudTransportException>(() => files.CheckAccessAsync());
        Assert.Equal(CloudFailureKind.DailyQuota, error.Kind); Assert.Equal(117401, error.Code); Assert.Equal(1, handler.Calls);
    }
    [Fact] public async Task Http429PreservesRetryAfterAndMakesOneRequest()
    {
        var handler = new Replies(_ => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rate limit") }; response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(3)); return response; });
        using var http = new HttpClient(handler); using var files = new WeiyunFileStore(new Auth(), client: http);
        var error = await Assert.ThrowsAsync<CloudTransportException>(() => files.CheckAccessAsync());
        Assert.Equal(CloudFailureKind.RateLimit, error.Kind); Assert.InRange(error.RetryAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(178), TimeSpan.FromSeconds(181)); Assert.Equal(1, handler.Calls);
    }
    [Fact] public void EventStreamSelectsMatchingResponseAfterNotifications()
    {
        var body = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\nevent: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{}}\n\n";
        Assert.Equal(7, WeiyunFileStore.ParseEnvelope(body, "text/event-stream", 7).GetProperty("id").GetInt32());
        Assert.Throws<InvalidDataException>(() => WeiyunFileStore.ParseEnvelope(body, "text/event-stream", 8));
    }
    [Fact] public async Task OpenEventStreamCompletesBeforeConnectionEof()
    {
        var bytes = Encoding.UTF8.GetBytes("data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"structuredContent\":{\"pdir_key\":\"root\",\"dir_list\":[],\"file_list\":[],\"finish_flag\":true}}}\n\n");
        var handler = new Replies(_ => { var content = new StreamContent(new OpenStream(bytes)); content.Headers.ContentType = new("text/event-stream"); return new(HttpStatusCode.OK) { Content = content }; });
        using var http = new HttpClient(handler); using var files = new WeiyunFileStore(new Auth(), client: http);
        await files.CheckAccessAsync().WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(1, handler.Calls);
    }
    [Fact] public async Task ConnectionReusesVerifiedKeysAndRejectsChangedDirectoryIdentity()
    {
        var changed = false;
        var handler = new Replies(request =>
        {
            using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()); var root = json.RootElement;
            var arguments = root.GetProperty("params").GetProperty("arguments");
            var key = arguments.TryGetProperty("dir_key", out var dir) ? dir.GetString()! : "root";
            var dirs = key == "root" ? new[] { new { dir_name = "ToDoList", dir_key = "app" } } : key == "app" ? new[] { new { dir_name = "Data", dir_key = "data" }, new { dir_name = "Sync", dir_key = "sync" } } : [];
            var result = new { pdir_key = changed && key == "data" ? "foreign" : key, dir_list = dirs, file_list = Array.Empty<object>(), finish_flag = true };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result = new { structuredContent = result } }), Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler); using var files = new WeiyunFileStore(new Auth(), client: http);
        await files.InitializeAsync(); Assert.Equal(2, handler.Calls);
        await files.InitializeAsync(); Assert.Equal(2, handler.Calls);
        await files.ListAsync(CloudArea.Data); await files.ListAsync(CloudArea.Sync); Assert.Equal(4, handler.Calls);
        changed = true; await Assert.ThrowsAsync<CloudTransportException>(() => files.ListAsync(CloudArea.Data));
    }
    private sealed class Replies(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return Task.FromResult(reply(request)); }
    }
    private sealed class Auth : ICloudAuthProvider
    {
        public CloudSession? Session => new("client", "access", "refresh", long.MaxValue, "root");
        public Task<string> GetAccessTokenAsync(bool refresh = false, CancellationToken ct = default) => Task.FromResult("access");
        public Task<CloudSession> LoginAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public void AttachIdentity(string rootKey) => Assert.Equal("root", rootKey);
    }
    private sealed class OpenStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { if (Position < Length) return await base.ReadAsync(buffer, cancellationToken); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
