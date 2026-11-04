using System.Security.Claims;
using IPop.Api.Endpoints;
using IPop.Application.Notifier;
using IPop.Application.Auth;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

public record NotifierWaitingRequest(string? DeviceKey);
public record NotifierRedeemRequest(string? Code, string? ShortCode, string? DeviceKey, string? Verifier);
public record NotifierAuthorizeRequest(string? Challenge, string? State, string? Redirect, string? Pc);

/// iPop Notifier sign-in from the browser and its update channel (1.4.3: OAuth loopback, see NotifierLoopback):
///   POST /api/notifier/link/authorize  browser: Authorize was clicked on /notifier/connect - mint the one-time code
///                                      (+ short code) for this account and the app's PKCE challenge
///   POST /api/notifier/link/redeem     anonymous: the app trades the code or short code + its verifier for a session
///   GET  /api/notifier/update          app: which version this person should run (+ "mandatory")
///   GET  /api/notifier/update/feed     app: the Velopack feed for that channel
///   GET  /api/notifier/releases        Admin: published versions, PCs per version, GitHub sync state, GPO msi
///   POST /api/notifier/releases/promote Admin: release the pilot to everyone
/// Older apps (1.3.x-1.4.2) still call the removed hand-off endpoints until they update: those answer 410 Gone with
/// a message (link/clear answers 204), so their browser sign-in fails cleanly while NIK + password sign-in, the
/// update check and device registration keep working. No endpoint here compares network addresses: behind the
/// proxy every client has the same one (the 1.4.2 Connect card reached another person's browser that way).
public static class NotifierLinkEndpoints
{
    public const string Fingerprint = "notifier:";
    public const string OutdatedMessage = "Update iPop Notifier to sign in with the browser, or sign in with your employee ID and password.";
    private static readonly object Outdated = new { code = "outdated", message = OutdatedMessage };
    /// Removed hand-off endpoints of 1.3.x-1.4.2.
    public static readonly string[] RetiredPost = ["waiting", "intent", "intent/check", "handoff", "poll", "claim", "take", "code", "approve", "deny", "connected"];
    public static readonly string[] RetiredGet = ["should", "status", "request/{id}"];

    public static IEndpointRouteBuilder MapNotifierLink(this IEndpointRouteBuilder app)
    {
        var store = NotifierLinkStore.Shared;
        var anon = app.MapGroup("/api/notifier/link").AllowAnonymous();
        foreach (var path in RetiredPost) anon.MapPost("/" + path, Gone);
        foreach (var path in RetiredGet) anon.MapGet("/" + path, Gone);

        anon.MapPost("/redeem", async (NotifierRedeemRequest req, HttpContext http, IConfiguration cfg, IAuthWorkflow auth,
            IPopDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            // 1.4.2 apps redeem a code they polled for with a request id; that hand-off is gone.
            if (req.Code is null && req.ShortCode is null) return Gone();
            var key = (req.DeviceKey ?? "").Trim();
            if (!ValidKey(key)) return Results.BadRequest(new { code = "invalid", message = "Invalid request." });
            if (!store.Allow("redeem:" + key, 20, now) || (req.ShortCode is not null && !store.Allow("redeem-short", 600, now)))
                return Results.Json(new { code = "busy", message = "Too many tries. Wait a minute, then try again." }, statusCode: StatusCodes.Status429TooManyRequests);
            var r = req.Code is not null ? store.Redeem(req.Code, req.Verifier, now) : store.RedeemShort(req.ShortCode, req.Verifier, now);
            if (r.Outcome != NotifierLinkStore.Outcome.Ok || r.Code is null)
                return Results.BadRequest(r.Outcome switch
                {
                    NotifierLinkStore.Outcome.Expired => new { code = "expired", message = "This sign-in code has expired. Choose Sign in with browser again." },
                    NotifierLinkStore.Outcome.Burned => new { code = "burned", message = "This sign-in code can no longer be used. Choose Sign in with browser again." },
                    _ => new { code = "invalid", message = req.Code is null
                        ? "That code didn't work. Check it, or choose Sign in with browser again."
                        : "This sign-in didn't work. Choose Sign in with browser again." },
                });

            var user = await db.Users.AsNoTracking().Where(u => u.Id == r.Code.UserId).Select(u => new { u.Nik, u.Name }).FirstOrDefaultAsync(ct);
            if (user is null) return Results.BadRequest(new { code = "expired", message = "This sign-in code has expired." });
            // The client address only goes to the audit log; it is never compared.
            var session = await auth.IssueSessionAsync(r.Code.UserId, Fingerprint + Machine(http), ClientAddress.Of(http, cfg), "auth.notifier_link", ct);
            if (session is null) return Results.BadRequest(new { code = "unavailable", message = "This account can't sign in right now." });
            return Results.Ok(new { session.AccessToken, session.RefreshToken, session.AccessTokenExpiresAt, session.MustChangePassword, nik = user.Nik, name = user.Name });
        });

        var g = app.MapGroup("/api/notifier").RequireAuthorization();

        g.MapPost("/link/authorize", async (NotifierAuthorizeRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (!NotifierLoopback.IsValidRedirect(req.Redirect) || !NotifierLoopback.IsSecret(req.State) || !NotifierLoopback.IsSecret(req.Challenge))
                return Results.BadRequest(new { code = "invalid", message = "This link isn't from iPop Notifier. Choose Sign in with browser in the app again." });
            var now = DateTime.UtcNow;
            if (!store.Allow("authorize:" + userId, 20, now)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var minted = store.Authorize(userId.Value, req.Challenge, now);
            if (minted is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return Results.Ok(new
            {
                code = minted.Code,
                shortCode = NotifierLoopback.FormatShortCode(minted.ShortCode),
                expiresIn = (int)NotifierLinkStore.CodeLifetime.TotalSeconds,
            });
        });

        // 1.3.x-1.4.2 apps call this right after signing in; there is no waiting list any more.
        g.MapPost("/link/clear", () => Results.NoContent());
        // An authenticated account can retire only its own row.
        g.MapPost("/devices/offline", async (NotifierWaitingRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var id = await MyId(http, db, ct);
            if (id is null || !ValidKey(req.DeviceKey ?? "")) return Results.BadRequest();
            await db.NotifierDevices.Where(d => d.UserId == id && d.DeviceKey == req.DeviceKey)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, DateTime.UtcNow - IPop.Core.Entities.NotifierDevice.OnlineWindow - TimeSpan.FromSeconds(1)), ct);
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

        g.MapGet("/releases", async (NotifierReleases releases, NotifierGithubSync sync, NotifierPublisher publisher, IPopDbContext db, CancellationToken ct) =>
        {
            var state = releases.Read();
            var status = sync.ReadStatus();
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
                sync = status is null ? null : new { status.LastRunAt, status.LastSuccessAt, status.LastError, status.Failures, status.Remote },
                gpoMsi = File.Exists(Path.Combine(releases.Folder, publisher.GpoMsi)) ? publisher.GpoMsi : null,
            });
        }).RequireAuthorization("AdminOnly");

        // Admin › Notifier "Release to everyone": the pilot (held + receipt-checked) becomes the public release.
        g.MapPost("/releases/promote", async (NotifierPublisher publisher, HttpContext http, ILoggerFactory logs, CancellationToken ct) =>
        {
            try
            {
                var outcome = await publisher.PromoteAsync(DateTime.UtcNow, ct);
                logs.CreateLogger("IPop.Api.Notifier").LogInformation("Notifier {Version} released to everyone by {Nik}", outcome.Version, http.User.FindFirstValue("nik"));
                return Results.Ok(new { version = outcome.Version });
            }
            catch (NotifierPublisher.PublishException e) { return Results.Conflict(new { code = "publish", message = e.Message }); }
        }).RequireAuthorization("AdminOnly");

        return app;
    }

    private static IResult Gone() => Results.Json(Outdated, statusCode: StatusCodes.Status410Gone);

    private static bool ValidKey(string key) => key.Length is >= 8 and <= 64 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string Machine(HttpContext http) =>
        string.Concat((http.Request.Headers["X-Notifier-Machine"].ToString() is { Length: > 0 } m ? m : "pc").Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(40));

    private static Task<Guid?> MyId(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirstValue("nik");
        return db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
