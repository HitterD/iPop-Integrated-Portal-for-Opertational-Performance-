using System.Security.Claims;
using IPop.Api.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class UploadAdmissionTests
{
    private static DefaultHttpContext Context(string user, string path = "/api/mail/attachments/upload")
    {
        var c = new DefaultHttpContext();
        c.Request.Method = "POST";
        c.Request.Path = path;
        c.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"));
        c.Response.Body = new MemoryStream();
        c.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return c;
    }
    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    {
        ["Uploads:MaxConcurrent"] = "2", ["Uploads:MaxConcurrentPerUser"] = "1",
    }).Build();

    [Fact]
    public async Task Busy_upload_slots_reject_extra_uploads_but_do_not_block_reads()
    {
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new UploadAdmissionMiddleware(async c =>
        {
            if (c.Request.Path == "/api/me/badges") return;
            Interlocked.Increment(ref entered);
            await release.Task;
        }, new UploadAdmissionService(Config(), TimeProvider.System));
        var a = middleware.InvokeAsync(Context("A"));
        var b = middleware.InvokeAsync(Context("B"));
        var sameUser = Context("A"); var thirdUser = Context("C"); var read = Context("D", "/api/me/badges");
        try
        {
            var extraA = middleware.InvokeAsync(sameUser);
            var extraC = middleware.InvokeAsync(thirdUser);
            await middleware.InvokeAsync(read).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(extraA.IsCompleted, "Per-user rejection waited for an upload slot.");
            Assert.True(extraC.IsCompleted, "Global rejection waited for an upload slot.");
            Assert.Equal(429, sameUser.Response.StatusCode);
            Assert.Equal(429, thirdUser.Response.StatusCode);
            Assert.Equal(200, read.Response.StatusCode);
            Assert.Equal(2, entered);
        }
        finally { release.TrySetResult(); await Task.WhenAll(a,b); }
    }

    [Fact]
    public async Task Reservations_share_the_budget_with_legacy_and_rejections_do_not_read_the_body()
    {
        var service = new UploadAdmissionService(Config(), TimeProvider.System);
        Assert.NotNull(service.Reserve("A", "/api/mail/attachments/upload"));
        Assert.Null(service.Reserve("A", "/api/mail/attachments/upload"));
        var sameOwner = Context("A");
        sameOwner.Request.Body = new UnreadableBody();
        await new UploadAdmissionMiddleware(_ => throw new Exception("Body parser was reached"), service).InvokeAsync(sameOwner);
        Assert.Equal(429, sameOwner.Response.StatusCode);
        Assert.NotNull(service.Reserve("B", "/api/chat/attachments/upload"));
        Assert.Null(service.Reserve("C", "/api/mail/attachments/upload"));
        var middleware = new UploadAdmissionMiddleware(_ => throw new Exception("Body parser was reached"), service);
        var legacy = Context("C");
        legacy.Request.Body = new UnreadableBody();
        await middleware.InvokeAsync(legacy);
        Assert.Equal(429, legacy.Response.StatusCode);
    }

    [Fact]
    public async Task Reservation_is_owner_and_route_bound_single_use_and_active_permits_never_expire()
    {
        var clock = new Clock();
        var service = new UploadAdmissionService(Config(), clock);
        var token = service.Reserve("A", "/api/mail/attachments/upload")!;
        var middleware = new UploadAdmissionMiddleware(_ => throw new Exception("Rejected request reached parser"), service);
        foreach (var (owner, route, value) in new[] {
            ("B", "/api/mail/attachments/upload", token),
            ("A", "/api/chat/attachments/upload", token),
            ("A", "/api/mail/attachments/upload", "unknown-after-restart") })
        {
            var context = Context(owner, route);
            context.Request.Headers[UploadAdmissionService.Header] = value;
            context.Request.Body = new UnreadableBody();
            await middleware.InvokeAsync(context);
            Assert.Equal(409, context.Response.StatusCode);
        }
        Assert.False(service.Cancel("B", "/api/mail/attachments/upload", token));
        Assert.False(service.Cancel("A", "/api/chat/attachments/upload", token));
        using (var active = service.TryStart("A", "/api/mail/attachments/upload", token))
        {
            Assert.NotNull(active);
            Assert.Null(service.TryStart("A", "/api/mail/attachments/upload", token));
            Assert.False(service.Cancel("A", "/api/mail/attachments/upload", token));
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Null(service.Reserve("A", "/api/mail/attachments/upload"));
        }
        Assert.Null(service.TryStart("A", "/api/mail/attachments/upload", token));
        Assert.NotNull(service.Reserve("A", "/api/mail/attachments/upload"));
    }

    [Fact]
    public void Expiry_and_cancel_release_reservations_without_tombstone_growth()
    {
        var clock = new Clock();
        var service = new UploadAdmissionService(Config(), clock);
        var expired = service.Reserve("A", "/api/mail/attachments/upload")!;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(service.TryStart("A", "/api/mail/attachments/upload", expired));
        for (var i = 0; i < 100; i++)
        {
            var token = service.Reserve("A", "/api/mail/attachments/upload")!;
            Assert.NotNull(token);
            Assert.True(service.Cancel("A", "/api/mail/attachments/upload", token));
            Assert.False(service.Cancel("A", "/api/mail/attachments/upload", token));
        }
        Assert.Null(service.Reserve("A", "/api/not-an-upload"));
        Assert.Null(service.TryStart("A", "/api/mail/attachments/upload", expired));
    }

    [Fact]
    public async Task Reservation_endpoints_authenticate_without_body_binding_and_cancel_using_the_same_service()
    {
        var service = new UploadAdmissionService(Config(), TimeProvider.System);
        var anonymous = Context("A"); anonymous.User = new ClaimsPrincipal();
        await IPop.Api.Endpoints.UploadAdmissionEndpoints.Reserve(anonymous, service);
        Assert.Equal(401, anonymous.Response.StatusCode);
        var reserve = Context("A");
        reserve.Request.QueryString = new QueryString("?route=/api/mail/attachments/upload");
        reserve.Request.Body = new UnreadableBody();
        await IPop.Api.Endpoints.UploadAdmissionEndpoints.Reserve(reserve, service);
        Assert.Equal(200, reserve.Response.StatusCode);
        reserve.Response.Body.Position = 0;
        using var json = await System.Text.Json.JsonDocument.ParseAsync(reserve.Response.Body);
        var token = json.RootElement.GetProperty("token").GetString()!;
        var busy = Context("A"); busy.Request.QueryString = reserve.Request.QueryString;
        await IPop.Api.Endpoints.UploadAdmissionEndpoints.Reserve(busy, service);
        Assert.Equal(429, busy.Response.StatusCode);
        Assert.Equal("5", busy.Response.Headers.RetryAfter.ToString());
        var cancel = Context("A"); cancel.Request.QueryString = reserve.Request.QueryString;
        cancel.Request.Headers[UploadAdmissionService.Header] = token;
        await IPop.Api.Endpoints.UploadAdmissionEndpoints.Cancel(cancel, service);
        Assert.Equal(204, cancel.Response.StatusCode);
        Assert.NotNull(service.Reserve("A", "/api/mail/attachments/upload"));
    }

    [Fact]
    public async Task Reserved_transfer_is_atomic_and_released_after_endpoint_failure()
    {
        var service = new UploadAdmissionService(Config(), TimeProvider.System);
        var token = service.Reserve("A", "/api/mail/attachments/upload")!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new UploadAdmissionMiddleware(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            throw new OperationCanceledException();
        }, service);
        var first = Context("A"); first.Request.Headers[UploadAdmissionService.Header] = token;
        var active = middleware.InvokeAsync(first);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var replay = Context("A"); replay.Request.Headers[UploadAdmissionService.Header] = token;
            await middleware.InvokeAsync(replay);
            Assert.Equal(409, replay.Response.StatusCode);
            Assert.Null(service.TryStart("A", "/api/mail/attachments/upload", null));
            Assert.False(service.Cancel("A", "/api/mail/attachments/upload", token));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => active);
        Assert.NotNull(service.Reserve("A", "/api/mail/attachments/upload"));
    }

    [Fact]
    public void Simultaneous_reservations_never_exceed_default_global_or_owner_budget()
    {
        var service = new UploadAdmissionService(new ConfigurationBuilder().Build(), TimeProvider.System);
        var tokens = new System.Collections.Concurrent.ConcurrentBag<(string Owner, string Token)>();
        Parallel.For(0, 64, i =>
        {
            var owner = (i % 8).ToString();
            if (service.Reserve(owner, "/api/mail/attachments/upload") is { } token) tokens.Add((owner, token));
        });
        Assert.Equal(4, tokens.Count);
        Assert.All(tokens.GroupBy(t => t.Owner), group => Assert.InRange(group.Count(), 1, 2));
        foreach (var (owner, token) in tokens) Assert.True(service.Cancel(owner, "/api/mail/attachments/upload", token));
        Assert.NotNull(service.Reserve("A", "/api/mail/attachments/upload"));
        Assert.NotNull(service.Reserve("A", "/api/mail/attachments/upload"));
        Assert.Null(service.Reserve("A", "/api/mail/attachments/upload"));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class UnreadableBody : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new Exception("Body read");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new Exception("Body read");
    }

    [Fact]
    public async Task Completed_or_cancelled_transfer_releases_its_permits()
    {
        var attempts = 0;
        var middleware = new UploadAdmissionMiddleware(c =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new OperationCanceledException();
            return Task.CompletedTask;
        }, new UploadAdmissionService(Config(), TimeProvider.System));
        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(Context("A")));
        var retry = Context("A"); await middleware.InvokeAsync(retry);
        Assert.Equal(200, retry.Response.StatusCode);
        Assert.Equal(2, attempts);
    }
}
