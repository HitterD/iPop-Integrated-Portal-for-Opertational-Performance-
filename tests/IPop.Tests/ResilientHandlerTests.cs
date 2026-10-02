using System.Net;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// A stalled read is retried instead of leaving the page on skeletons; writes never are.
public class ResilientHandlerTests
{
    private sealed class Scripted(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            var step = steps[Math.Min(Seen.Count - 1, steps.Length - 1)];
            return Task.FromResult(step(request));
        }
    }

    private static HttpResponseMessage Ok(HttpRequestMessage _) => new(HttpStatusCode.OK);
    private static HttpResponseMessage Unavailable(HttpRequestMessage _) => new(HttpStatusCode.ServiceUnavailable);
    // What HttpClient surfaces when the per-attempt CancelAfter fires.
    private static HttpResponseMessage Stalled(HttpRequestMessage _) => throw new TaskCanceledException("attempt timed out");
    private static HttpResponseMessage Unreachable(HttpRequestMessage _) => throw new HttpRequestException("connection refused");

    private static (HttpClient Client, Scripted Inner, List<TimeSpan> Waits) Build(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
    {
        var inner = new Scripted(steps);
        var waits = new List<TimeSpan>();
        var handler = new ResilientHandler(inner) { Delay = (d, _) => { waits.Add(d); return Task.CompletedTask; } };
        return (new HttpClient(handler) { BaseAddress = new Uri("http://ipop.test/") }, inner, waits);
    }

    [Fact]
    public async Task Get_that_stalls_is_sent_again_and_succeeds()
    {
        var (http, inner, waits) = Build(Stalled, Ok);
        var res = await http.GetAsync("api/dashboard");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, inner.Seen.Count);
        Assert.Single(waits);
    }

    [Fact]
    public async Task Retry_keeps_the_bearer_header()
    {
        var (http, inner, _) = Build(Unavailable, Ok);
        var req = new HttpRequestMessage(HttpMethod.Get, "api/mail?folder=inbox");
        req.Headers.Authorization = new("Bearer", "t0k");
        await http.SendAsync(req);
        Assert.Equal(2, inner.Seen.Count);
        Assert.NotSame(inner.Seen[0], inner.Seen[1]);
        Assert.Equal("t0k", inner.Seen[1].Headers.Authorization?.Parameter);
        Assert.Equal(inner.Seen[0].RequestUri, inner.Seen[1].RequestUri);
    }

    [Fact]
    public async Task Gives_up_after_two_retries_with_a_timeout()
    {
        var (http, inner, waits) = Build(Stalled);
        await Assert.ThrowsAsync<TimeoutException>(() => http.GetAsync("api/projects"));
        Assert.Equal(1 + ResilientHandler.MaxRetries, inner.Seen.Count);
        Assert.Equal(ResilientHandler.MaxRetries, waits.Count);
    }

    [Fact]
    public async Task Last_503_is_returned_not_thrown()
    {
        var (http, inner, _) = Build(Unavailable);
        var res = await http.GetAsync("api/news");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal(3, inner.Seen.Count);
    }

    [Fact]
    public async Task Network_error_is_retried_then_surfaces()
    {
        var (http, inner, _) = Build(Unreachable);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("api/notes/sidebar"));
        Assert.Equal(3, inner.Seen.Count);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Writes_are_never_retried(string method)
    {
        var (http, inner, _) = Build(Unavailable, Ok);
        var res = await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), "api/mail/send"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Single(inner.Seen);
    }

    [Fact]
    public async Task Stalled_write_is_not_resent()
    {
        var (http, inner, _) = Build(Stalled, Ok);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.PostAsync("api/chat/rooms/x/messages", null));
        Assert.Single(inner.Seen);
    }

    [Theory]
    [InlineData("api/files/download?path=/a.pdf")]
    [InlineData("api/projects/export")]
    [InlineData("api/news/videos/1/stream")]
    [InlineData("api/mail/attachments/9")]
    [InlineData("api/files/preview?path=/a.png")]
    [InlineData("api/files/thumb?path=/a.png")]
    [InlineData("hubs/chat/negotiate")]
    public async Task Long_running_and_non_api_gets_are_left_alone(string url)
    {
        var (http, inner, _) = Build(Unavailable, Ok);
        await http.GetAsync(url);
        Assert.Single(inner.Seen);
    }

    [Fact]
    public async Task Caller_cancel_is_not_retried()
    {
        using var cts = new CancellationTokenSource();
        var inner = new Scripted(_ => { cts.Cancel(); throw new TaskCanceledException(); });
        var http = new HttpClient(new ResilientHandler(inner) { Delay = (_, _) => Task.CompletedTask }) { BaseAddress = new Uri("http://ipop.test/") };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync("api/dashboard", cts.Token));
        Assert.Single(inner.Seen);
    }
}
