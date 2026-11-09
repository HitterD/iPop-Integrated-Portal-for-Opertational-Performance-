using System.Security.Claims;

namespace IPop.Api.Diagnostics;

/// Bounds multipart work before endpoint form parsing allocates temporary files or starts NAS transfers.
/// This is a per-process budget: multiple API replicas would require a separate deployment-wide budget.
public sealed class UploadAdmissionMiddleware
{
    private readonly RequestDelegate next;
    private readonly int maxConcurrent;
    private readonly int maxPerUser;
    private readonly object gate = new();
    private readonly Dictionary<string, int> users = new(StringComparer.Ordinal);
    private int running;

    public UploadAdmissionMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        this.next = next;
        maxConcurrent = Math.Clamp(configuration.GetValue("Uploads:MaxConcurrent", 4), 1, 64);
        maxPerUser = Math.Clamp(configuration.GetValue("Uploads:MaxConcurrentPerUser", 2), 1, maxConcurrent);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !IsUpload(context.Request.Path)
            || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }
        var user = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub") ?? context.User.FindFirstValue("nik") ?? "unknown";
        bool admitted;
        lock (gate)
        {
            var count = users.GetValueOrDefault(user);
            admitted = running < maxConcurrent && count < maxPerUser;
            if (admitted) { running++; users[user] = count + 1; }
        }
        if (!admitted)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "5";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "upload_busy", message = "Other uploads are still running. Please try again in a few seconds.",
            }, context.RequestAborted);
            return;
        }
        try { await next(context); }
        finally
        {
            lock (gate)
            {
                running--;
                var count = users[user] - 1;
                if (count == 0) users.Remove(user); else users[user] = count;
            }
        }
    }

    private static bool IsUpload(PathString path) =>
        path.Equals("/api/mail/attachments/upload", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/chat/attachments/upload", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/files/upload", StringComparison.OrdinalIgnoreCase);
}
