using System.Security.Claims;
using IPop.Api.Endpoints;
using IPop.Application.Auth;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

public record NotifierWaitingRequest(string? DeviceKey);
public record NotifierRedeemRequest(string? Code, string? DeviceKey, string? Verifier, string? RequestId);
public record NotifierLinkRequest(string? Challenge, string? RequestId);

/// iPop Notifier sign-in from the browser and its update channel (docs/notifier/PLAN.md, 1.3.0):
///   POST /api/notifier/link/waiting   anonymous: a signed-out app says it is waiting on this address
///   GET  /api/notifier/link/should    browser: is there an app here to sign in (or one on another account)?
///   POST /api/notifier/link/code      browser: one-time code for ipop-notifier://signin?code=…
///   POST /api/notifier/link/redeem    anonymous: the app trades the code for its own session
///   GET  /api/notifier/update         app: which version this person should run (+ "mandatory")
///   GET  /api/notifier/update/feed    app: the Velopack feed for that channel
///   GET  /api/notifier/releases       Admin: published versions and PCs per version
public static class NotifierLinkEndpoints
{
    /// A browser login counts as "fresh" for an account switch for this long (switching a signed-in app needs it).
    public static readonly TimeSpan FreshLogin = TimeSpan.FromSeconds(60);
    public const string Fingerprint = "notifier:";

    public static IEndpointRouteBuilder MapNotifierLink(this IEndpointRouteBuilder app)
    {
        var store = NotifierLinkStore.Shared;
        var anon = app.MapGroup("/api/notifier/link").AllowAnonymous();

        anon.MapPost("/waiting", (NotifierWaitingRequest req, HttpContext http, IConfiguration cfg) =>
        {
            var ip = ClientAddress.Of(http, cfg);
            var key = (req.DeviceKey ?? "").Trim();
            if (ip is null || !ValidKey(key)) return Results.BadRequest();
            if (!store.Allow("wait:" + ip, 30, DateTime.UtcNow)) return Results.StatusCode(429);
            store.MarkWaiting(key, ip, DateTime.UtcNow);
            return Results.NoContent();
        });

        anon.MapPost("/redeem", async (NotifierRedeemRequest req, HttpContext http, IConfiguration cfg, IAuthWorkflow auth,
            IPopDbContext db, CancellationToken ct) =>
        {
            var ip = ClientAddress.Of(http, cfg);
            var now = DateTime.UtcNow;
            if (!store.Allow("redeem:" + ip, 20, now)) return Results.StatusCode(429);
            var key = (req.DeviceKey ?? "").Trim();
            if (!ValidKey(key)) return Results.BadRequest(new { message = "Invalid request." });
            var code = store.Redeem(req.Code, req.Verifier, req.RequestId, now);
            if (code is null) return Results.BadRequest(new { code = "expired", message = "This sign-in link has expired. Open iPop in the browser again." });

            var nik = await db.Users.AsNoTracking().Where(u => u.Id == code.UserId).Select(u => u.Nik).FirstOrDefaultAsync(ct);
            if (nik is null) return Results.BadRequest(new { code = "expired", message = "This sign-in link has expired." });
            var session = await auth.IssueSessionAsync(code.UserId, Fingerprint + Machine(http), ip,
                "auth.notifier_link", ct);
            if (session is null) return Results.BadRequest(new { code = "unavailable", message = "This account can't sign in right now." });
            store.ClearWaiting(key);
            var name = await db.Users.AsNoTracking().Where(u => u.Id == code.UserId).Select(u => u.Name).FirstOrDefaultAsync(ct);
            return Results.Ok(new { session.AccessToken, session.RefreshToken, session.AccessTokenExpiresAt, session.MustChangePassword, nik, name });
        });

        var g = app.MapGroup("/api/notifier").RequireAuthorization();

        // Ask only when it can work: a signed-out app waiting at this address, or an app here signed in as someone
        // else (the web account changed). An app already on this account needs nothing.
        g.MapGet("/link/should", async (HttpContext http, IConfiguration cfg, IPopDbContext db, CancellationToken ct) =>
        {
            var ip = ClientAddress.Of(http, cfg);
            var userId = await MyId(http, db, ct);
            if (userId is null || ip is null) return Results.Ok(new { link = false, reason = (string?)null });
            var now = DateTime.UtcNow;
            if (store.AnyWaiting(ip, now)) return Results.Ok(new { link = true, reason = (string?)"waiting" });
            var since = now - IPop.Core.Entities.NotifierDevice.OnlineWindow;
            var here = await db.NotifierDevices.AsNoTracking().Where(d => d.RemoteIp == ip && d.LastSeenAt >= since)
                .Select(d => d.UserId).ToListAsync(ct);
            var other = here.Count > 0 && !here.Contains(userId.Value);
            return Results.Ok(new { link = other, reason = other ? "other" : null });
        });

        g.MapPost("/link/code", async (NotifierLinkRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var now = DateTime.UtcNow;
            if (!store.Allow("code:" + userId, 10, now)) return Results.StatusCode(429);
            var code = store.Issue(userId.Value, req.Challenge ?? "", req.RequestId ?? "", now);
            return code is null ? Results.BadRequest() : Results.Ok(new { code, expiresIn = (int)NotifierLinkStore.CodeLifetime.TotalSeconds });
        });

        // Presence is advisory only. An authenticated account can retire only its own row.
        g.MapPost("/link/clear", async (NotifierWaitingRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var id = await MyId(http, db, ct);
            if (id is null || !ValidKey(req.DeviceKey ?? "")) return Results.BadRequest();
            store.ClearWaiting(req.DeviceKey!);
            return Results.NoContent();
        });
        g.MapPost("/devices/offline", async (NotifierWaitingRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var id = await MyId(http, db, ct);
            if (id is null || !ValidKey(req.DeviceKey ?? "")) return Results.BadRequest();
            await db.NotifierDevices.Where(d => d.UserId == id && d.DeviceKey == req.DeviceKey)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, DateTime.UtcNow - IPop.Core.Entities.NotifierDevice.OnlineWindow - TimeSpan.FromSeconds(1)), ct);
            store.ClearWaiting(req.DeviceKey!);
            return Results.NoContent();
        });

        g.MapGet("/installer-version", (NotifierReleases releases, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Ok(new { version = releases.Read().All?.Version });
        });

        g.MapGet("/update", async (HttpContext http, IPopDbContext db, NotifierReleases releases, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var pilot = await NotifierEndpoints.IsIctOrAdminAsync(db, userId.Value, http.User.IsInRole("Admin"), ct);
            var (_, target) = releases.FeedFor(pilot);
            return Results.Ok(new { version = target?.Version, mandatory = target?.Mandatory == true, channel = pilot ? "pilot" : "all" });
        });

        g.MapGet("/update/feed", async (HttpContext http, IPopDbContext db, NotifierReleases releases, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var pilot = await NotifierEndpoints.IsIctOrAdminAsync(db, userId.Value, http.User.IsInRole("Admin"), ct);
            var (feed, _) = releases.FeedFor(pilot);
            http.Response.Headers.CacheControl = "no-cache";
            return feed is null ? Results.NotFound() : Results.Text(feed, "application/json");
        });

        g.MapGet("/releases", async (NotifierReleases releases, IPopDbContext db, CancellationToken ct) =>
        {
            var state = releases.Read();
            var since = DateTime.UtcNow - TimeSpan.FromDays(14);
            var versions = await db.NotifierDevices.AsNoTracking().Where(d => d.LastSeenAt >= since)
                .GroupBy(d => d.AppVersion ?? "")
                .Select(x => new { version = x.Key, pcs = x.Count(), online = x.Count(d => d.LastSeenAt >= DateTime.UtcNow - IPop.Core.Entities.NotifierDevice.OnlineWindow) })
                .ToListAsync(ct);
            return Results.Ok(new
            {
                all = state.All,
                pilot = state.Pilot,
                versions = versions.OrderByDescending(v => NotifierReleases.IsVersion(v.version) ? System.Version.Parse(v.version) : new System.Version(0, 0)).ToList(),
            });
        }).RequireAuthorization("AdminOnly");

        return app;
    }

    /// The browser's login (its session family: refreshes rotate rows but keep the family) started within
    /// <see cref="FreshLogin"/>. The first row of a family is the password sign-in itself.
    public static async Task<bool> FreshLoginAsync(IPopDbContext db, Guid userId, string? family, DateTime now, CancellationToken ct)
    {
        if (!Guid.TryParse(family, out var f)) return false;
        var started = await db.AuthSessions.AsNoTracking().Where(s => s.UserId == userId && s.FamilyId == f)
            .OrderBy(s => s.CreatedAt).Select(s => new { s.CreatedAt, s.DeviceFingerprint }).FirstOrDefaultAsync(ct);
        return started is not null && now - started.CreatedAt <= FreshLogin
            && (started.DeviceFingerprint is null || !started.DeviceFingerprint.StartsWith(Fingerprint, StringComparison.Ordinal));
    }

    private static bool ValidKey(string key) => key.Length is >= 8 and <= 64 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string Machine(HttpContext http) =>
        string.Concat((http.Request.Headers["X-Notifier-Machine"].ToString() is { Length: > 0 } m ? m : "pc").Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(40));

    private static Task<Guid?> MyId(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirstValue("nik");
        return db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
