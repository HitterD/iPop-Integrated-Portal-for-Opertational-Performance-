using IPop.Api.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace IPop.Tests;

/// Requests over a second are written to logs/slow-requests.log; hubs and streams never are.
public class SlowRequestLogTests
{
    private sealed class Env(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "IPop.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }

    [Theory]
    [InlineData("/hubs/chat", true)]
    [InlineData("/api/news/videos/1/stream", true)]
    [InlineData("/api/files/download", true)]
    [InlineData("/api/projects/export", true)]
    [InlineData("/api/dashboard", false)]
    [InlineData("/api/mail", false)]
    public void Long_running_routes_are_excluded(string path, bool excluded)
        => Assert.Equal(excluded, SlowRequestLog.IsExcluded(path));

    [Fact]
    public async Task Slow_request_is_written_with_method_path_status_and_time()
    {
        var root = Path.Combine(Path.GetTempPath(), "ipop-slowlog-" + Guid.NewGuid().ToString("N"));
        var log = new SlowRequestLog(new Env(root));
        using var cts = new CancellationTokenSource();
        await log.StartAsync(cts.Token);
        try
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/api/dashboard";
            ctx.Response.StatusCode = 200;
            log.Record(ctx, TimeSpan.FromMilliseconds(7512));

            var file = Path.Combine(root, "logs", "slow-requests.log");
            for (var i = 0; i < 50 && !File.Exists(file); i++) await Task.Delay(50);
            var line = Assert.Single(await File.ReadAllLinesAsync(file));
            Assert.Contains("\t7512ms\t200\tGET\t/api/dashboard", line);
        }
        finally
        {
            await log.StopAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }
}
