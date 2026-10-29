using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using IPop.Api.Endpoints;
using IPop.Application.Auth;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

public record NotifierWaitingRequest(string? DeviceKey);
public record NotifierRedeemRequest(string? Code, string? DeviceKey, string? Verifier, string? RequestId);
public record NotifierLinkRequest(string? Challenge, string? RequestId);
public record NotifierIntentRequest(string? DeviceKey, string? Scheme);
public record NotifierIntentCheck(string? DeviceKey, string? Intent, bool Reserve);
public record NotifierHandoffRequest(string? DeviceKey, string? Intent, string? Nonce, string? RequestId, string? Challenge);
public record NotifierClaimRequest(bool Opened);
public record NotifierTakeRequest(string? Nonce);
public record NotifierConnectedRequest(string? RequestId);

/// iPop Notifier sign-in from the browser and its update channel (docs/notifier/PLAN.md, 1.3.0):
///   POST /api/notifier/link/waiting   anonymous: a signed-out app says it is waiting on this address (discovery only)
///   POST /api/notifier/link/intent    anonymous: "Sign in with browser" was clicked; open iPop tabs here hear it (AppHub)
///   POST /api/notifier/link/intent/check  anonymous: did a tab take it? (reserve = the app opens one tab itself)
///   POST /api/notifier/link/handoff   anonymous: the app answers prepare?nonce=… with its PKCE request (no 2nd tab)
///   GET  /api/notifier/link/should    browser: was sign-in asked for here (or did this browser just sign in as someone else)?
///   POST /api/notifier/link/claim     browser: this tab takes the intent (one tab wins)
///   POST /api/notifier/link/take      browser: the PKCE request the app posted for this tab's nonce
///   POST /api/notifier/link/code      browser: one-time code for ipop-notifier://signin?code=…
///   POST /api/notifier/link/redeem    anonymous: the app trades the code for its own session
///   POST /api/notifier/link/connected app (new session): confirm, so the tab can say "connected"
///   GET  /api/notifier/link/status    browser: is the app connected for my request?
///   GET  /api/notifier/update         app: which version this person should run (+ "mandatory")
///   GET  /api/notifier/update/feed    app: the Velopack feed for that channel
///   GET  /api/notifier/releases       Admin: published versions and PCs per version
public static class NotifierLinkEndpoints
{
    /// A browser login counts as "fresh" for an account switch for this long (switching a signed-in app needs it).
    public static readonly TimeSpan FreshLogin = TimeSpan.FromSeconds(60);
    public const string Fingerprint = "notifier:";
    /// AppHub event to the open iPop tabs on the address of a "Sign in with browser" click.
    public const string IntentEvent = "notifierLinkIntent";

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

        anon.MapPost("/intent", async (NotifierIntentRequest req, HttpContext http, IConfiguration cfg,
            IHubContext<IPop.Api.Realtime.AppHub> hub, CancellationToken ct) =>
        {
            var ip = ClientAddress.Of(http, cfg);
            var key = (req.DeviceKey ?? "").Trim();
            var now = DateTime.UtcNow;
            if (ip is null || !ValidKey(key)) return Results.BadRequest();
            if (!store.Allow("intent:" + ip, 10, now)) return Results.StatusCode(429);
            var intent = store.RegisterIntent(key, ip, req.Scheme ?? "ipop-notifier", now);
            if (intent is null) return Results.BadRequest();
            store.MarkWaiting(key, ip, now);
            var tabs = store.TabsAt(ip);
            if (tabs.Count > 0)
                try { await hub.Clients.Clients(tabs).SendAsync(IntentEvent, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
            return Results.Ok(new { intent = intent.Id, expiresIn = (int)NotifierLinkStore.IntentLifetime.TotalSeconds, tabs = tabs.Count });
        });

        anon.MapPost("/intent/check", (NotifierIntentCheck req, HttpContext http, IConfiguration cfg) =>
        {
            var ip = ClientAddress.Of(http, cfg);
            var key = (req.DeviceKey ?? "").Trim();
            if (ip is null || !ValidKey(key)) return Results.BadRequest();
            if (!store.Allow("check:" + ip, 60, DateTime.UtcNow)) return Results.StatusCode(429);
            var claimed = store.CheckIntent(key, req.Intent, req.Reserve, DateTime.UtcNow);
            return claimed is null ? Results.NotFound() : Results.Ok(new { claimed });
        });

        // The nonce never left this browser except through the protocol link to the app, so only that app can answer
        // for it, and only the tab holding the nonce can take the answer. Posting authorizes nothing by itself: the
        // code is still minted by the signed-in tab for the app's PKCE challenge.
        anon.MapPost("/handoff", (NotifierHandoffRequest req, HttpContext http, IConfiguration cfg) =>
        {
            var ip = ClientAddress.Of(http, cfg);
            var key = (req.DeviceKey ?? "").Trim();
            if (ip is null || !ValidKey(key)) return Results.BadRequest();
            if (!store.Allow("handoff:" + ip, 20, DateTime.UtcNow)) return Results.StatusCode(429);
            return store.PutHandoff(key, req.Intent, req.Nonce, req.RequestId, req.Challenge, DateTime.UtcNow) ? Results.NoContent() : Results.BadRequest();
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
            store.ClearIntent(key);
            var name = await db.Users.AsNoTracking().Where(u => u.Id == code.UserId).Select(u => u.Name).FirstOrDefaultAsync(ct);
            return Results.Ok(new { session.AccessToken, session.RefreshToken, session.AccessTokenExpiresAt, session.MustChangePassword, nik, name });
        });

        var g = app.MapGroup("/api/notifier").RequireAuthorization();

        // The browser may ask "Open iPop Notifier?" only when someone just asked for it: a "Sign in with browser"
        // click (or first run) on this address in the last 2 minutes ("intent"), or this browser itself signed in
        // within the last minute as someone other than the app here ("other": the web account switch, which the app
        // still confirms locally before it switches). A waiting heartbeat alone never prompts any more (1.4.0).
        // opened=1: the tab was opened by "Sign in with browser" in a 1.3.0 app (it knows no intent and, signed out,
        // can't fetch the update). That tab alone may still prompt while an app waits here ("legacy").
        g.MapGet("/link/should", async (bool? opened, HttpContext http, IConfiguration cfg, IPopDbContext db, CancellationToken ct) =>
        {
            var ip = ClientAddress.Of(http, cfg);
            var userId = await MyId(http, db, ct);
            if (userId is null || ip is null) return Results.Ok(new { link = false, reason = (string?)null });
            var now = DateTime.UtcNow;
            var reason = await ShouldReasonAsync(store, db, userId.Value, ip,
                http.User.FindFirstValue(IPop.Infrastructure.Auth.AccessTokenGuard.SessionClaim), opened == true, now, ct);
            return Results.Ok(new { link = reason is not null, reason });
        });

        g.MapPost("/link/claim", async (NotifierClaimRequest req, HttpContext http, IConfiguration cfg, IPopDbContext db, CancellationToken ct) =>
        {
            var ip = ClientAddress.Of(http, cfg);
            var userId = await MyId(http, db, ct);
            var now = DateTime.UtcNow;
            if (ip is null || userId is null || !store.Allow("claim:" + ip, 30, now)) return Results.Ok(new { claimed = false, scheme = (string?)null });
            var intent = store.ClaimIntent(ip, req.Opened, userId.Value, now);
            return Results.Ok(new { claimed = intent is not null, scheme = intent?.Scheme });
        });

        g.MapPost("/link/take", async (NotifierTakeRequest req, HttpContext http, IConfiguration cfg, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (!store.Allow("take:" + userId, 120, DateTime.UtcNow)) return Results.StatusCode(429);
            var h = store.TakeHandoff(req.Nonce, userId.Value, ClientAddress.Of(http, cfg), DateTime.UtcNow);
            return h is null ? Results.NoContent() : Results.Ok(new { requestId = h.RequestId, challenge = h.Challenge });
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

    /// Why this browser may show "Open iPop Notifier?" now, or null: never just because an app waits here.
    public static async Task<string?> ShouldReasonAsync(NotifierLinkStore store, IPopDbContext db, Guid userId, string ip,
        string? family, bool opened, DateTime now, CancellationToken ct)
    {
        if (store.HasFreshIntent(ip, now)) return "intent";
        if (opened && store.AnyWaiting(ip, now)) return "legacy";
        if (!await FreshLoginAsync(db, userId, family, now, ct)) return null;
        var since = now - IPop.Core.Entities.NotifierDevice.OnlineWindow;
        var here = await db.NotifierDevices.AsNoTracking().Where(d => d.RemoteIp == ip && d.LastSeenAt >= since)
            .Select(d => d.UserId).ToListAsync(ct);
        return here.Count > 0 && !here.Contains(userId) ? "other" : null;
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
