using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using IPop.Api.Endpoints;
using IPop.Application.Auth;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

public record NotifierWaitingRequest(string? DeviceKey);
public record NotifierRedeemRequest(string? Code, string? DeviceKey, string? Verifier, string? RequestId);
public record NotifierIntentRequest(string? DeviceKey, string? Scheme, string? Challenge, string? RequestId, string? PcName);
public record NotifierPollRequest(string? DeviceKey, string? RequestId, bool Reserve);
public record NotifierDecisionRequest(string? Request);
public record NotifierConnectedRequest(string? RequestId);

/// iPop Notifier sign-in from the browser and its update channel (1.4.2: a connect card, no protocol launch):
///   POST /api/notifier/link/intent    anonymous: "Sign in with browser" - the app registers its connect request
///                                     (request id, PKCE challenge, PC name); open iPop tabs here hear it (AppHub)
///   POST /api/notifier/link/poll      anonymous (device key): pending | claimed | approved + code | denied | expired
///   POST /api/notifier/link/redeem    anonymous: the app trades the code (+ its verifier) for its own session
///   POST /api/notifier/link/claim     browser: this tab takes the request and shows the card (one tab wins)
///   GET  /api/notifier/link/request/{id}  browser: what the card shows (PC name, age, same address, expired)
///   POST /api/notifier/link/approve   browser: Connect was clicked - mint the code for this account
///   POST /api/notifier/link/deny      browser: Cancel was clicked
///   POST /api/notifier/link/connected app (new session): confirm, so the card can say "Connected"
///   GET  /api/notifier/link/status    browser: is the app connected for the request I approved?
///   GET  /api/notifier/update         app: which version this person should run (+ "mandatory")
///   GET  /api/notifier/update/feed    app: the Velopack feed for that channel
///   GET  /api/notifier/releases       Admin: published versions and PCs per version
/// 1.3.x/1.4.1 apps still call intent (old body), intent/check, handoff and waiting until they update: those answer
/// 410 Gone (link/clear answers 204), so browser sign-in in those versions fails cleanly while NIK + password sign-in
/// and the update check keep working.
public static class NotifierLinkEndpoints
{
    public const string Fingerprint = "notifier:";
    /// AppHub event to the open iPop tabs on the address of a "Sign in with browser" click.
    public const string IntentEvent = "notifierLinkIntent";
    private static readonly object Outdated = new { code = "outdated", message = "Update iPop Notifier to sign in with the browser, or sign in with your employee ID and password." };

    public static IEndpointRouteBuilder MapNotifierLink(this IEndpointRouteBuilder app)
    {
        var store = NotifierLinkStore.Shared;
        var anon = app.MapGroup("/api/notifier/link").AllowAnonymous();

        // Old apps (1.3.x-1.4.1): their browser hand-off needed a protocol launch from the web, which is gone.
        anon.MapPost("/waiting", () => Results.Json(Outdated, statusCode: StatusCodes.Status410Gone));
        anon.MapPost("/intent/check", () => Results.Json(Outdated, statusCode: StatusCodes.Status410Gone));
        anon.MapPost("/handoff", () => Results.Json(Outdated, statusCode: StatusCodes.Status410Gone));

        anon.MapPost("/intent", async (NotifierIntentRequest req, HttpContext http, IConfiguration cfg,
            IHubContext<IPop.Api.Realtime.AppHub> hub, CancellationToken ct) =>
        {
            var ip = ClientAddress.Of(http, cfg);
            var key = (req.DeviceKey ?? "").Trim();
            var now = DateTime.UtcNow;
            if (ip is null || !ValidKey(key)) return Results.BadRequest();
            if (req.Challenge is null && req.RequestId is null) return Results.Json(Outdated, statusCode: StatusCodes.Status410Gone);
            if (!store.Allow("intent:" + ip, 10, now)) return Results.StatusCode(429);
            var r = store.Register(key, ip, req.RequestId, req.Challenge, req.PcName, req.Scheme ?? "ipop-notifier", now);
            if (r is null) return Results.BadRequest();
            var tabs = store.TabsAt(ip);
            if (tabs.Count > 0)
                try { await hub.Clients.Clients(tabs).SendAsync(IntentEvent, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
            return Results.Ok(new { request = r.Id, expiresIn = (int)NotifierLinkStore.RequestLifetime.TotalSeconds, tabs = tabs.Count });
        });

        anon.MapPost("/poll", (NotifierPollRequest req) =>
        {
            var key = (req.DeviceKey ?? "").Trim();
            if (!ValidKey(key)) return Results.BadRequest();
            if (!store.Allow("poll:" + key, 150, DateTime.UtcNow)) return Results.StatusCode(429);
            var p = store.Poll(key, req.RequestId, req.Reserve, DateTime.UtcNow);
            return p is null ? Results.NotFound() : Results.Ok(new { state = p.State, code = p.Code });
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
            var name = await db.Users.AsNoTracking().Where(u => u.Id == code.UserId).Select(u => u.Name).FirstOrDefaultAsync(ct);
            return Results.Ok(new { session.AccessToken, session.RefreshToken, session.AccessTokenExpiresAt, session.MustChangePassword, nik, name });
        });

        var g = app.MapGroup("/api/notifier").RequireAuthorization();

        g.MapPost("/link/claim", async (HttpContext http, IConfiguration cfg, IPopDbContext db, CancellationToken ct) =>
        {
            var ip = ClientAddress.Of(http, cfg);
            var userId = await MyId(http, db, ct);
            var now = DateTime.UtcNow;
            if (ip is null || userId is null || !store.Allow("claim:" + ip, 30, now)) return Results.Ok(new { claimed = false, request = (string?)null });
            var r = store.Claim(ip, now);
            return Results.Ok(new { claimed = r is not null, request = r?.Id });
        });

        g.MapGet("/link/request/{id}", async (string id, HttpContext http, IConfiguration cfg, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (!store.Allow("card:" + userId, 60, DateTime.UtcNow)) return Results.StatusCode(429);
            var r = store.Find(id);
            if (r is null) return Results.NotFound();
            var now = DateTime.UtcNow;
            // "Connect as <me>?" when this PC's Notifier was last signed in as someone else (that name is not shown).
            var last = await db.NotifierDevices.AsNoTracking().Where(d => d.DeviceKey == r.DeviceKey)
                .OrderByDescending(d => d.LastSeenAt).Select(d => (Guid?)d.UserId).FirstOrDefaultAsync(ct);
            var state = r.State switch
            {
                NotifierLinkStore.State.Denied => "denied",
                NotifierLinkStore.State.Approved or NotifierLinkStore.State.Collected => r.ApprovedBy == userId ? "approved" : "used",
                _ => NotifierLinkStore.IsLive(r, now) ? "pending" : "expired",
            };
            return Results.Ok(new
            {
                pcName = r.PcName,
                createdAt = r.CreatedAt,
                ageSeconds = (int)Math.Max(0, (now - r.CreatedAt).TotalSeconds),
                sameAddress = ClientAddress.Of(http, cfg) == r.Ip,
                expired = state == "expired",
                state,
                switching = last is { } u && u != userId,
            });
        });

        g.MapPost("/link/approve", async (NotifierDecisionRequest req, HttpContext http, IConfiguration cfg, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (!store.Allow("approve:" + userId, 20, DateTime.UtcNow)) return Results.StatusCode(429);
            return Answer(store.Approve(req.Request, userId.Value, ClientAddress.Of(http, cfg), DateTime.UtcNow));
        });

        g.MapPost("/link/deny", async (NotifierDecisionRequest req, HttpContext http, IConfiguration cfg, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (!store.Allow("approve:" + userId, 20, DateTime.UtcNow)) return Results.StatusCode(429);
            return Answer(store.Deny(req.Request, ClientAddress.Of(http, cfg), DateTime.UtcNow));
        });

        g.MapGet("/link/status", async (string? request, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var st = store.Status(request, userId.Value, DateTime.UtcNow);
            return Results.Ok(new { connected = st?.ConnectedAt is not null, name = st?.Name, known = st is not null });
        });

        // The app, with the session it just redeemed, confirms the request (after any local account-switch prompt).
        g.MapPost("/link/connected", async (NotifierConnectedRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var name = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync(ct);
            return store.MarkConnected(req.RequestId, userId.Value, name, DateTime.UtcNow) ? Results.NoContent() : Results.NotFound();
        });

        // 1.3.x-1.4.1 apps call this right after signing in; there is no waiting list any more.
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

    private static IResult Answer(NotifierLinkStore.Decision d) => d switch
    {
        NotifierLinkStore.Decision.Ok => Results.NoContent(),
        NotifierLinkStore.Decision.NotFound => Results.NotFound(new { code = "not_found", message = "This request was not found." }),
        NotifierLinkStore.Decision.Expired => Results.Json(new { code = "expired", message = "This request expired." }, statusCode: StatusCodes.Status410Gone),
        NotifierLinkStore.Decision.OtherAddress => Results.Json(new { code = "other_pc", message = "Open this page on the PC where iPop Notifier is waiting." }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Conflict(new { code = "used", message = "This request was already answered." }),
    };

    private static bool ValidKey(string key) => key.Length is >= 8 and <= 64 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string Machine(HttpContext http) =>
        string.Concat((http.Request.Headers["X-Notifier-Machine"].ToString() is { Length: > 0 } m ? m : "pc").Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(40));

    private static Task<Guid?> MyId(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirstValue("nik");
        return db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
