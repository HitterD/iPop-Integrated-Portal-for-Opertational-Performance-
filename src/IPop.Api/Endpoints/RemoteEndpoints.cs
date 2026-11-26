using System.Security.Claims;
using IPop.Api.Notifier;
using IPop.Application.Attachments;
using IPop.Application.Auth;
using IPop.Application.Calls;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public record RemoteStartRequest(Guid? DeviceId, string? Mode, string? Target = null, string? Password = null, bool? AskUser = null,
    string? FromMachine = null);
public record RemoteAnswerRequest(bool Allow, IReadOnlyList<string>? Permissions = null);
public record RemoteEndRequest(string? Files);
public record RemoteUnattendedRequest(bool Unattended);
public record RemoteHelperRequest(bool Allowed);

/// Remote help in the iPop Notifier (docs/mockups/notifier/remote.html). Admin/ICT ask a user's PC; the PC answers
/// (or skips the prompt when Admin marked it unattended); both sides then join one LiveKit room with tokens that only
/// let the user's PC publish the screen. Video, input, clipboard, files and chat go peer to peer through LiveKit —
/// iPop keeps only the consent + log row (no content, chat never stored).
public static class RemoteEndpoints
{
    public const string ConnectRateLimitPolicy = "remote-connect";
    public static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(30);
    /// LiveKit join token: checked only when connecting (a running session stays; LiveKit refreshes the token of a
    /// connected client itself). Short, so a token taken from a log or memory is useless after 10 minutes (review PR #5).
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);
    /// Without a heartbeat from the user's PC for this long, an "active" session is treated as over.
    public static readonly TimeSpan AliveWindow = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan LogRetention = TimeSpan.FromDays(365);
    public const long MediaQuotaBytes = 2L * 1024 * 1024 * 1024;
    public const long MediaMaxBytes = 200L * 1024 * 1024;
    private static readonly string[] MediaTypes = ["image/jpeg", "image/png", "image/gif", "image/webp", "video/mp4", "video/webm"];

    public static IEndpointRouteBuilder MapRemote(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifier/remote").RequireAuthorization();

        // ICT → ask a PC (by device, 9-digit ID or computer name). Unattended PCs need the access password instead of
        // the prompt; it is checked here (RemoteStart) and every attempt is logged.
        g.MapPost("/sessions", async (RemoteStartRequest req, HttpContext http, IPopDbContext db, ILiveKitTokenService lk, IPasswordHasher hasher,
            IHubContext<IPop.Api.Notifications.NotificationHub> hub, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            if (!await CanHelpAsync(db, me.Value.Id, http.User.IsInRole("Admin"), ct)) return Results.Forbid();
            if (!lk.IsConfigured) return Results.Json(new { message = "Remote help needs LiveKit, which isn't set up on this server." }, statusCode: 503);
            var now = DateTime.UtcNow;
            var r = await RemoteStart.StartAsync(db, hasher, me.Value.Id,
                new RemoteStart.Request(req.DeviceId, req.Target, req.Mode, req.Password, req.AskUser == true, req.FromMachine), now, ct);
            if (r.Session is not { } session) return Results.Json(r.Body, statusCode: r.Status);
            await hub.Clients.User(r.Device!.UserId.ToString()).SendAsync("remoteRequest", new
            {
                sessionId = session.Id, deviceKey = r.Device.DeviceKey, helper = me.Value.Name, helperInitials = Initials(me.Value.Name),
                mode = session.Mode, unattended = session.Unattended, joining = r.Joining, expiresAt = now + AskTimeout,
                permissions = session.Permissions, from = Cut(req.FromMachine, 64),
            }, ct);
            return Results.Ok(r.Body);
        }).RequireRateLimiting(ConnectRateLimitPolicy);

        // User's PC answers the prompt.
        g.MapPost("/sessions/{id:guid}/answer", async (Guid id, RemoteAnswerRequest req, HttpContext http, IPopDbContext db,
            IHubContext<IPop.Api.Notifications.NotificationHub> hub, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var s = await db.RemoteSessions.FirstOrDefaultAsync(x => x.Id == id && x.TargetUserId == me.Value.Id, ct);
            if (s is null) return Results.NotFound();
            var status = Answer(s, req.Allow, DateTime.UtcNow);
            // What the user ticked in the prompt (old Notifiers send nothing = everything, as their prompt said).
            if (status == "active" && s.Permissions is null)
                s.Permissions = RemoteAccess.Permissions(req.Permissions ?? RemoteAccess.AllPermissions.Split(','), s.Mode);
            await db.SaveChangesAsync(ct);
            await hub.Clients.User(s.HelperId.ToString()).SendAsync("remoteAnswer", new { sessionId = s.Id, status, permissions = s.Permissions }, ct);
            return Results.Ok(new { status });
        });

        // Either side: the LiveKit room + token once the session is active.
        g.MapGet("/sessions/{id:guid}/join", async (Guid id, HttpContext http, IPopDbContext db, ILiveKitTokenService lk, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var s = await db.RemoteSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null || (s.HelperId != me.Value.Id && s.TargetUserId != me.Value.Id)) return Results.NotFound();
            if (s.Status != "active") return Results.Json(new { status = s.Status }, statusCode: 409);
            var isHost = s.TargetUserId == me.Value.Id;
            // Only the PC being helped publishes video; the helper sends input/clipboard/files/chat as data.
            var token = lk.GenerateToken(s.Room, (isHost ? "host:" : "helper:") + me.Value.Id, me.Value.Name, TokenLifetime,
                sources: isHost ? ["screen_share"] : []);
            var remoteId = await db.NotifierDevices.AsNoTracking().Where(d => d.Id == s.DeviceId).Select(d => d.Machine != null ? d.Machine.ConnectId : null).FirstOrDefaultAsync(ct);
            return Results.Ok(new { url = lk.PublicUrl, token, room = s.Room, role = isHost ? "host" : "helper", mode = s.Mode,
                permissions = s.Permissions ?? RemoteAccess.AllPermissions, remoteId, unattended = s.Unattended,
                helper = await db.Users.AsNoTracking().Where(u => u.Id == s.HelperId).Select(u => u.Name).FirstAsync(ct) });
        });

        // User's PC: "still sharing" every 30 s. 410 = the session is over (ended elsewhere) → the PC stops sharing.
        g.MapGet("/sessions/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var s = await db.RemoteSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && (x.HelperId == me.Value.Id || x.TargetUserId == me.Value.Id), ct);
            return s is null ? Results.NotFound() : Results.Ok(new { s.Status, s.EndedBy, s.StartedAt, s.Permissions });
        });

        // User's PC: a "My media" picture/video the helper of THIS session chose to show (review PR #5). The attachment must
        // belong to that helper and the session must be active; any other id from the data channel gets 404.
        g.MapGet("/sessions/{id:guid}/media/{attachmentId:guid}", async (Guid id, Guid attachmentId, HttpContext http, IPopDbContext db,
            IAttachmentStore store, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var s = await db.RemoteSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TargetUserId == me.Value.Id && x.Status == "active", ct);
            if (s is null) return Results.NotFound();
            if (!await db.RemoteMedia.AsNoTracking().AnyAsync(m => m.AttachmentId == attachmentId && m.OwnerId == s.HelperId, ct)) return Results.NotFound();
            var file = await store.OpenAsync(attachmentId, ct);
            return file is { } f ? Results.Stream(f.Content, f.Meta.ContentType) : Results.NotFound();
        });

        g.MapPost("/sessions/{id:guid}/alive", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var s = await db.RemoteSessions.FirstOrDefaultAsync(x => x.Id == id && x.TargetUserId == me.Value.Id, ct);
            if (s is null) return Results.NotFound();
            if (s.Status != "active") return Results.StatusCode(410);
            s.AliveAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/sessions/{id:guid}/end", async (Guid id, RemoteEndRequest req, HttpContext http, IPopDbContext db,
            IHubContext<IPop.Api.Notifications.NotificationHub> hub, IPop.Infrastructure.Calls.ILiveKitRoomAdmin liveKit, ILogger<RemoteSession> log, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var s = await db.RemoteSessions.FirstOrDefaultAsync(x => x.Id == id && (x.HelperId == me.Value.Id || x.TargetUserId == me.Value.Id), ct);
            if (s is null) return Results.NotFound();
            End(s, s.HelperId == me.Value.Id ? "helper" : "user", req.Files, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            var other = s.HelperId == me.Value.Id ? s.TargetUserId : s.HelperId;
            await hub.Clients.User(other.ToString()).SendAsync("remoteEnded", new { sessionId = s.Id, endedBy = s.EndedBy }, ct);
            // The other side also leaves LiveKit on the server, whatever its app does with the hub message. A second ICT
            // watching the same room keeps their own session, so only this session's two identities go.
            if (liveKit.IsConfigured)
                foreach (var identity in new[] { "host:" + s.TargetUserId, "helper:" + s.HelperId })
                {
                    if (await db.RemoteSessions.AnyAsync(x => x.Room == s.Room && x.Id != s.Id && x.Status == "active"
                        && (identity == "host:" + x.TargetUserId || identity == "helper:" + x.HelperId), ct)) continue;
                    try { await liveKit.RemoveAsync(s.Room, identity, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Remote help: couldn't remove {Identity} from {Room}.", identity, s.Room); }
                }
            return Results.NoContent();
        });

        // Log: Admin sees every session, ICT their own, a user who helped them (Settings → Account).
        g.MapGet("/log", async (Guid? deviceId, string? scope, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var isAdmin = http.User.IsInRole("Admin");
            var q = db.RemoteSessions.AsNoTracking().Where(x => x.Status != "asking");
            if (scope == "mine") q = q.Where(x => x.TargetUserId == me.Value.Id);
            else if (!isAdmin)
            {
                if (!await CanHelpAsync(db, me.Value.Id, false, ct)) return Results.Forbid();
                q = q.Where(x => x.HelperId == me.Value.Id);
            }
            if (deviceId is { } d) q = q.Where(x => x.DeviceId == d);
            var rows = await q.OrderByDescending(x => x.RequestedAt).Take(200)
                .Select(x => new { x.Id, helper = x.Helper.Name, user = x.TargetUser.Name, machine = x.Device.MachineName, x.Mode, x.Status,
                    x.Unattended, x.RequestedAt, x.StartedAt, x.EndedAt, x.EndedBy, x.Files, x.Permissions,
                    remoteId = x.Device.Machine != null ? x.Device.Machine.ConnectId : null, x.DeviceId })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // Admin only: the PC's "Connect without asking the user" switch (BIGIS #8: on the PC, not the device; it takes
        // effect only with a password — RemoteMachineEndpoints). Turning it off revokes unattended sessions.
        g.MapPut("/devices/{id:guid}/unattended", async (Guid id, RemoteUnattendedRequest req, HttpContext http, IPopDbContext db,
            IHubContext<IPop.Api.Notifications.NotificationHub> hub, IPop.Infrastructure.Calls.ILiveKitRoomAdmin liveKit, ILogger<RemoteSession> log, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("Admin")) return Results.Forbid();
            var d = await db.NotifierDevices.Include(x => x.Machine).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d?.Machine is not { } m) return Results.NotFound();
            m.Unattended = req.Unattended;
            m.SetById = (await Me(http, db, ct))?.Id ?? m.SetById;
            if (!req.Unattended) d.Unattended = false; // the old per-device flag must not switch it back on
            var revoked = req.Unattended ? [] : await RemoteAccess.RevokeAsync(db, m.Id, DateTime.UtcNow, ct);
            await db.SaveChangesAsync(ct);
            await RemoteMachineEndpoints.TellRevokedAsync(hub, liveKit, log, revoked, ct);
            log.LogInformation("Remote help: {Admin} set unattended={Value} on {Machine} ({RemoteId}).", http.User.FindFirstValue("nik"), req.Unattended, m.ComputerName, m.ConnectId);
            return Results.NoContent();
        });

        // Admin: who may remote (ICT/Admin people) and the per-person switch.
        g.MapGet("/helpers", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("Admin")) return Results.Forbid();
            var blocked = await db.RemoteHelperBlocks.AsNoTracking().Select(b => b.UserId).ToListAsync(ct);
            var people = await db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => new { u.Id, u.Name }).ToListAsync(ct);
            var rows = new List<object>();
            foreach (var p in people)
                if (await NotifierEndpoints.IsIctOrAdminAsync(db, p.Id, false, ct) || blocked.Contains(p.Id))
                    rows.Add(new { p.Id, p.Name, allowed = !blocked.Contains(p.Id) });
            return Results.Ok(rows);
        });

        g.MapPut("/helpers/{userId:guid}", async (Guid userId, RemoteHelperRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("Admin")) return Results.Forbid();
            var me = await Me(http, db, ct);
            var row = await db.RemoteHelperBlocks.FirstOrDefaultAsync(b => b.UserId == userId, ct);
            if (req.Allowed && row is not null) db.RemoteHelperBlocks.Remove(row);
            if (!req.Allowed && row is null) db.RemoteHelperBlocks.Add(new RemoteHelperBlock { UserId = userId, SetById = me!.Value.Id, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ── My media ─────────────────────────────────────────────────────────
        g.MapGet("/media", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            if (!await CanHelpAsync(db, me.Value.Id, http.User.IsInRole("Admin"), ct)) return Results.Forbid();
            var items = await db.RemoteMedia.AsNoTracking().Where(m => m.OwnerId == me.Value.Id).OrderByDescending(m => m.CreatedAt)
                .Select(m => new { m.Id, m.AttachmentId, m.Name, m.ContentType, m.Size, m.CreatedAt }).ToListAsync(ct);
            return Results.Ok(new { items, used = items.Sum(i => i.Size), quota = MediaQuotaBytes });
        });

        g.MapPost("/media", async (HttpContext http, IPopDbContext db, IAttachmentStore store, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            if (!await CanHelpAsync(db, me.Value.Id, http.User.IsInRole("Admin"), ct)) return Results.Forbid();
            if (!http.Request.HasFormContentType) return Results.BadRequest(new { message = "Choose a picture or video." });
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            var check = CheckMedia(file?.FileName, file?.ContentType, file?.Length ?? 0,
                await db.RemoteMedia.Where(m => m.OwnerId == me.Value.Id).SumAsync(m => (long?)m.Size, ct) ?? 0);
            if (check is not null) return Results.BadRequest(new { message = check });
            await using var stream = file!.OpenReadStream();
            var stored = await store.PutAsync(AttachmentAreas.Remote, file.FileName, file.ContentType, stream, me.Value.Id, ct);
            var row = new RemoteMedia { Id = Guid.NewGuid(), OwnerId = me.Value.Id, AttachmentId = stored.Id, Name = Path.GetFileNameWithoutExtension(file.FileName),
                ContentType = file.ContentType, Size = stored.Size, CreatedAt = DateTime.UtcNow };
            db.RemoteMedia.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { row.Id, row.AttachmentId, row.Name, row.ContentType, row.Size, row.CreatedAt });
        }).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MediaMaxBytes + 1_000_000));

        g.MapDelete("/media/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, IAttachmentStore store, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var m = await db.RemoteMedia.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == me.Value.Id, ct);
            if (m is null) return Results.NotFound();
            db.RemoteMedia.Remove(m);
            await db.SaveChangesAsync(ct);
            try { await store.DeleteAsync(m.AttachmentId, ct); } catch { }
            return Results.NoContent();
        });

        return app;
    }

    public static string RoomName(Guid sessionId) => "remote-" + sessionId.ToString("N");

    /// "active" sessions on this PC whose heartbeat stopped are closed (logged as ended by "timeout").
    public static async Task<int> CloseStaleAsync(IPopDbContext db, Guid deviceId, DateTime now, CancellationToken ct)
    {
        var stale = await db.RemoteSessions
            .Where(x => x.DeviceId == deviceId && x.Status == "active" && (x.AliveAt ?? x.StartedAt) <= now - AliveWindow)
            .ToListAsync(ct);
        foreach (var s in stale) { s.Status = "ended"; s.EndedAt = s.AliveAt ?? s.StartedAt ?? now; s.EndedBy = "timeout"; }
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    /// "active" when allowed in time, "declined" otherwise, "expired" after the 30 s prompt. Idempotent once decided.
    public static string Answer(RemoteSession s, bool allow, DateTime now)
    {
        if (s.Status != "asking") return s.Status;
        if (now > s.RequestedAt + AskTimeout + TimeSpan.FromSeconds(5)) { s.Status = "expired"; s.EndedAt = now; s.EndedBy = "timeout"; return s.Status; }
        if (allow) { s.Status = "active"; s.StartedAt = now; s.AliveAt = now; }
        else { s.Status = "declined"; s.EndedAt = now; s.EndedBy = "user"; }
        return s.Status;
    }

    public static void End(RemoteSession s, string by, string? files, DateTime now)
    {
        if (s.Status is "ended" or "declined" or "expired") return;
        s.Status = s.Status == "asking" ? "expired" : "ended";
        s.EndedAt = now;
        s.EndedBy = by;
        if (!string.IsNullOrWhiteSpace(files))
            s.Files = string.Join('\n', files.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(f => Path.GetFileName(f)).Where(f => f.Length > 0).Take(50)) is { Length: > 0 } list ? list[..Math.Min(list.Length, 4000)] : null;
    }

    /// Null when the upload is fine, else the message for the person.
    public static string? CheckMedia(string? name, string? type, long size, long used)
    {
        if (string.IsNullOrWhiteSpace(name) || size <= 0) return "Choose a picture or video.";
        if (type is null || !MediaTypes.Contains(type.ToLowerInvariant())) return "Use JPG, PNG, GIF, WEBP, MP4 or WEBM.";
        if (size > MediaMaxBytes) return "That file is over 200 MB.";
        if (used + size > MediaQuotaBytes) return "My media is full (2 GB). Delete something first.";
        return null;
    }

    /// Same people as the Devices list (Admin role + ICT departments), unless Admin switched remote off for them.
    public static async Task<bool> CanHelpAsync(IPopDbContext db, Guid userId, bool isAdmin, CancellationToken ct) =>
        await NotifierEndpoints.IsIctOrAdminAsync(db, userId, isAdmin, ct)
        && !await db.RemoteHelperBlocks.AsNoTracking().AnyAsync(b => b.UserId == userId, ct);

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, max)];

    private static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    internal static async Task<(Guid Id, string Name)?> Me(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirstValue("nik");
        var row = await db.Users.AsNoTracking().Where(u => u.Nik == nik && u.IsActive).Select(u => new { u.Id, u.Name }).FirstOrDefaultAsync(ct);
        return row is null ? null : (row.Id, row.Name);
    }
}

/// "My media" files: readable by their owner, and by a user whose PC that owner is helping right now
/// (the user's app shows the picture or video). Nobody else.
public sealed class RemoteMediaPolicy(IPopDbContext db) : IAttachmentAccessPolicy
{
    public string Area => AttachmentAreas.Remote;

    public async Task<bool> CanReadAsync(Guid attachmentId, Guid userId, CancellationToken ct)
    {
        var owner = await db.RemoteMedia.AsNoTracking().Where(m => m.AttachmentId == attachmentId).Select(m => (Guid?)m.OwnerId).FirstOrDefaultAsync(ct);
        if (owner is null) return false;
        if (owner == userId) return true;
        return await db.RemoteSessions.AsNoTracking().AnyAsync(s => s.HelperId == owner && s.TargetUserId == userId && s.Status == "active", ct);
    }
}
