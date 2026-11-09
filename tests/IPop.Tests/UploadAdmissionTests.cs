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
        }, Config());
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
    public async Task Completed_or_cancelled_transfer_releases_its_permits()
    {
        var attempts = 0;
        var middleware = new UploadAdmissionMiddleware(c =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new OperationCanceledException();
            return Task.CompletedTask;
        }, Config());
        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(Context("A")));
        var retry = Context("A"); await middleware.InvokeAsync(retry);
        Assert.Equal(200, retry.Response.StatusCode);
        Assert.Equal(2, attempts);
    }
}
