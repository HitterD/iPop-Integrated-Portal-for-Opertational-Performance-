using System.Diagnostics;
using System.Threading.Channels;

namespace IPop.Api.Diagnostics;

/// Writes every API request slower than <see cref="Threshold"/> to logs/slow-requests.log, so
/// "the page hung for a while" can be traced to a route afterwards. Hubs, streams and
/// downloads are long by design and are left out. Lines go through a channel so a burst of
/// slow requests never waits on the disk; the file rolls over at 10 MB (one previous copy).
public sealed class SlowRequestLog : BackgroundService
{
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(1);
    private const long MaxBytes = 10 * 1024 * 1024;

    private readonly Channel<string> lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly string path;

    public SlowRequestLog(IWebHostEnvironment env)
        => path = Path.Combine(env.ContentRootPath, "logs", "slow-requests.log");

    public static bool IsExcluded(PathString p)
    {
        var s = p.Value ?? "";
        return s.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase)
            || s.Contains("/stream", StringComparison.OrdinalIgnoreCase)
            || s.Contains("/download", StringComparison.OrdinalIgnoreCase)
            || s.Contains("/videos/", StringComparison.OrdinalIgnoreCase)
            || s.Contains("/export", StringComparison.OrdinalIgnoreCase);
    }

    public void Record(HttpContext ctx, TimeSpan elapsed)
        => lines.Writer.TryWrite(
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\t{elapsed.TotalMilliseconds:0}ms\t{ctx.Response.StatusCode}\t{ctx.Request.Method}\t{ctx.Request.Path}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await foreach (var line in lines.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                        File.Move(path, path + ".1", overwrite: true);
                    await File.AppendAllTextAsync(path, line + Environment.NewLine, stoppingToken);
                }
                catch (IOException) { /* a locked or full disk must never affect requests */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    public static IApplicationBuilder UseSlowRequestLog(IApplicationBuilder app)
    {
        var log = app.ApplicationServices.GetRequiredService<SlowRequestLog>();
        return app.Use(async (ctx, next) =>
        {
            if (IsExcluded(ctx.Request.Path)) { await next(); return; }
            var started = Stopwatch.GetTimestamp();
            try { await next(); }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed >= Threshold) log.Record(ctx, elapsed);
            }
        });
    }
}
