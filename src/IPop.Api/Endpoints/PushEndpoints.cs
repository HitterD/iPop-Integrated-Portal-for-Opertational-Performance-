using IPop.Api.Calls;
using IPop.Application.Calls;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications.Push;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Web Push (Windows toasts): VAPID public key, this browser's subscription, the device list,
/// the chat-preview privacy switch, and the call toast's "Tolak" action.
public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPush(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/push");

        // Public: the browser needs the key before it can subscribe. enabled=false → push is off server-side.
        g.MapGet("/config", (PushOptions o) =>
            Results.Ok(new PushConfigDto(o.IsConfigured, o.IsConfigured ? o.VapidPublicKey : null)));

        // Register (or refresh) this browser. The endpoint is unique: a browser shared by two
        // accounts belongs to whoever signed in last, so toasts never reach the previous user.
        g.MapPost("/subscriptions", async (PushSubscribeRequest req, HttpContext http, IPopDbContext db, PushOptions o, CancellationToken ct) =>
        {
            if (!o.IsConfigured) return Results.Json(new { code = "push_disabled" }, statusCode: 503);
            if (!IsValidEndpoint(req.Endpoint) || string.IsNullOrWhiteSpace(req.P256dh) || string.IsNullOrWhiteSpace(req.Auth)
                || req.P256dh.Length > 200 || req.Auth.Length > 100)
                return Results.BadRequest(new { code = "invalid_subscription" });
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();

            var now = DateTime.UtcNow;
            var sub = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == req.Endpoint, ct);
            if (sub is null)
            {
                sub = new PushSubscription { Id = Guid.NewGuid(), Endpoint = req.Endpoint, P256dh = req.P256dh, Auth = req.Auth, CreatedAt = now };
                db.PushSubscriptions.Add(sub);
            }
            sub.UserId = userId.Value;
            sub.P256dh = req.P256dh;
            sub.Auth = req.Auth;
            sub.Label = DeviceLabel(http.Request.Headers.UserAgent.ToString());
            sub.ApiBase = NormalizeApiBase(req.ApiBase);
            sub.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = sub.Id });
        }).RequireAuthorization();

        // ── Native Android app (FCM) ──
        // Public: the app initialises Firebase with these client values at runtime (no google-services.json in the APK).
        g.MapGet("/native/config", (FcmOptions f) => Results.Ok(f.IsConfigured
            ? new NativePushConfigDto(true, f.ProjectId, f.ApplicationId, f.ApiKey, f.SenderId)
            : new NativePushConfigDto(false, null, null, null, null)));

        // Register (or refresh) this phone's FCM token. Like browsers, a token belongs to whoever signed in last.
        g.MapPost("/native/devices", async (NativeDeviceRequest req, HttpContext http, IPopDbContext db, FcmOptions f, CancellationToken ct) =>
        {
            if (!f.IsConfigured) return Results.Json(new { code = "push_disabled" }, statusCode: 503);
            if (string.IsNullOrWhiteSpace(req.Token) || req.Token.Length > 900 || req.Token.Any(char.IsWhiteSpace))
                return Results.BadRequest(new { code = "invalid_token" });
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var endpoint = NativePush.EndpointOf(req.Token);
            var now = DateTime.UtcNow;
            var sub = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
            if (sub is null)
            {
                sub = new PushSubscription { Id = Guid.NewGuid(), Endpoint = endpoint, P256dh = "-", Auth = "-", CreatedAt = now };
                db.PushSubscriptions.Add(sub);
            }
            sub.UserId = userId.Value;
            var model = (req.DeviceName ?? "").Trim();
            sub.Label = model.Length is > 0 and <= 60 ? $"{NativePush.Label} · {model}" : NativePush.Label;
            sub.ApiBase = null;
            sub.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = sub.Id });
        }).RequireAuthorization();

        g.MapPost("/native/devices/remove", async (NativeDeviceRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Token)) return Results.NoContent();
            var endpoint = NativePush.EndpointOf(req.Token);
            await db.PushSubscriptions.Where(s => s.UserId == userId && s.Endpoint == endpoint).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // Native app update check: the release script writes AndroidApp:LatestPath (default C:/ipop-ops/android/latest.json)
        // next to ipop.apk: {"versionCode":2,"versionName":"2.0.0","sizeBytes":0,"url":"/download/ipop.apk","notes":[],"minSupported":2}.
        app.MapGet("/api/app/android/latest", (IConfiguration cfg) =>
        {
            var path = cfg["AndroidApp:LatestPath"] ?? "C:/ipop-ops/android/latest.json";
            if (!File.Exists(path)) return Results.NotFound();
            try
            {
                var latest = System.Text.Json.JsonSerializer.Deserialize<AndroidLatestDto>(File.ReadAllText(path),
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                return latest is { VersionCode: > 0 } ? Results.Ok(latest) : Results.NotFound();
            }
            catch (System.Text.Json.JsonException) { return Results.NotFound(); }
        }).AllowAnonymous();

        // Sign-out / turning toasts off in this browser.
        g.MapPost("/subscriptions/remove", async (PushUnsubscribeRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            await db.PushSubscriptions.Where(s => s.UserId == userId && s.Endpoint == req.Endpoint).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapGet("/devices", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var rows = await db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == userId)
                .OrderByDescending(s => s.LastSeenAt)
                .Select(s => new PushDeviceDto(s.Id, s.Label, s.Endpoint, s.CreatedAt, s.LastSeenAt))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).RequireAuthorization();

        g.MapDelete("/devices/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var n = await db.PushSubscriptions.Where(s => s.Id == id && s.UserId == userId).ExecuteDeleteAsync(ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireAuthorization();

        // Test toast to every browser of the caller — the "Send test" button in Settings.
        g.MapPost("/test", async (HttpContext http, IPopDbContext db, PushQueue push, PushOptions o, FcmOptions f, CancellationToken ct) =>
        {
            if (!o.IsConfigured && !f.IsConfigured) return Results.Json(new { code = "push_disabled" }, statusCode: 503);
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            // Durable before 202: the returned jobId shows up in /diagnostics as it is delivered.
            var jobId = await push.EnqueueAsync(db, "test", null, ct, [userId.Value]);
            return Results.Accepted(value: new PushTestResultDto(jobId));
        }).RequireAuthorization();

        // This user's own browsers and recent toast attempts — sanitized: no endpoint, keys, titles or bodies.
        // "accepted" means the push service took the message; it does not prove Windows displayed it.
        g.MapGet("/diagnostics", async (Guid? current, HttpContext http, IPopDbContext db, PushOptions o, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await PushDiagnostics.ForUserAsync(db, userId.Value, current, o.IsConfigured, DateTime.UtcNow, ct));
        }).RequireAuthorization();

        // Admin: fleet-wide aggregate only — counts, no users, devices, names or content.
        app.MapGet("/api/admin/push/diagnostics", async (IPopDbContext db, PushOptions o, CancellationToken ct) =>
            Results.Ok(await PushDiagnostics.AggregateAsync(db, o.IsConfigured, DateTime.UtcNow, ct)))
            .RequireAuthorization("AdminOnly");

        g.MapGet("/preferences", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var show = await db.NotificationPreferences.AsNoTracking().Where(p => p.UserId == userId)
                .Select(p => (bool?)p.PushShowPreview).FirstOrDefaultAsync(ct);
            return Results.Ok(new PushPreferencesDto(show ?? true));
        }).RequireAuthorization();

        g.MapPut("/preferences", async (PushPreferencesDto req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var p = await db.NotificationPreferences.FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (p is null)
            {
                p = new NotificationPreference { Id = Guid.NewGuid(), UserId = userId.Value };
                db.NotificationPreferences.Add(p);
            }
            p.PushShowPreview = req.ShowPreview;
            p.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new PushPreferencesDto(p.PushShowPreview));
        }).RequireAuthorization();

        // "Tolak" on the call toast. The service worker has no JWT, so the push carried a
        // short-lived token bound to (call, user); it only ever declines that one ring.
        g.MapPost("/call-action", async (PushCallActionRequest req, IDataProtectionProvider dp, ICallService calls,
            IPopDbContext db, IHubContext<CallHub> hub, PushQueue push, CancellationToken ct) =>
        {
            if (req.Action != "decline") return Results.BadRequest();
            if (PushDispatcher.ReadCallActionToken(dp, req.Token) is not { } t) return Results.Unauthorized();
            var result = await calls.SignalAsync(t.UserId, new CallSignalRequest(t.CallId, "decline"), ct);
            if (result.Room is null) return Results.NoContent(); // already answered/ended — nothing to do
            await CallEndpoints.BroadcastGroupAsync(hub, db, result.Room, ct, push);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("push-action");

        return app;
    }

    /// Push services are https endpoints on the public internet (FCM, Mozilla, WNS, Apple).
    internal static bool IsValidEndpoint(string? endpoint) =>
        !string.IsNullOrWhiteSpace(endpoint) && endpoint.Length <= 1000 && !NativePush.IsNative(endpoint)
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static string? NormalizeApiBase(string? apiBase) =>
        Uri.TryCreate(apiBase, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u.GetLeftPart(UriPartial.Authority)
            : null;

    /// "Chrome · Windows" from the User-Agent — for the device list only.
    internal static string DeviceLabel(string ua)
    {
        var browser = ua.Contains("Edg/") ? "Edge"
            : ua.Contains("OPR/") ? "Opera"
            : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") ? "Chrome"
            : ua.Contains("Safari/") ? "Safari"
            : "Browser";
        var os = ua.Contains("Windows") ? "Windows"
            : ua.Contains("Android") ? "Android"
            : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS"
            : ua.Contains("Mac OS X") ? "macOS"
            : ua.Contains("Linux") ? "Linux"
            : "";
        return os.Length == 0 ? browser : $"{browser} · {os}";
    }

    private static Task<Guid?> MyId(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        return db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    public sealed record PushConfigDto(bool Enabled, string? PublicKey);
    public sealed record PushSubscribeRequest(string Endpoint, string P256dh, string Auth, string? ApiBase);
    public sealed record PushUnsubscribeRequest(string Endpoint);
    public sealed record PushDeviceDto(Guid Id, string Label, string Endpoint, DateTime CreatedAt, DateTime LastSeenAt);
    public sealed record PushPreferencesDto(bool ShowPreview);
    public sealed record PushCallActionRequest(string Token, string Action);
    public sealed record PushTestResultDto(Guid JobId);
    public sealed record NativePushConfigDto(bool Enabled, string? ProjectId, string? ApplicationId, string? ApiKey, string? SenderId);
    public sealed record AndroidLatestDto(int VersionCode, string VersionName, long SizeBytes, string Url, string[]? Notes, int MinSupported);
    public sealed record NativeDeviceRequest(string Token, string? Platform, string? AppVersion, string? DeviceName);
}
