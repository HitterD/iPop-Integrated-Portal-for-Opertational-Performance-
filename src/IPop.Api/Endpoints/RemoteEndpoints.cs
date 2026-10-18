using System.Security.Claims;
using IPop.Application.Attachments;
using IPop.Application.Calls;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public record RemoteStartRequest(Guid DeviceId, string? Mode);
public record RemoteAnswerRequest(bool Allow);
public record RemoteEndRequest(string? Files);
public record RemoteUnattendedRequest(bool Unattended);
public record RemoteHelperRequest(bool Allowed);

/// Remote help in the iPop Notifier (docs/mockups/notifier/remote.html). Admin/ICT ask a user's PC; the PC answers
/// (or skips the prompt when Admin marked it unattended); both sides then join one LiveKit room with tokens that only
/// let the user's PC publish the screen. Video, input, clipboard, files and chat go peer to peer through LiveKit —
/// iPop keeps only the consent + log row (no content, chat never stored).
public static class RemoteEndpoints
{
    public static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(4);
    public static readonly TimeSpan LogRetention = TimeSpan.FromDays(365);
    public const long MediaQuotaBytes = 2L * 1024 * 1024 * 1024;
    public const long MediaMaxBytes = 200L * 1024 * 1024;
    private static readonly string[] MediaTypes = ["image/jpeg", "image/png", "image/gif", "image/webp", "video/mp4", "video/webm"];

    public static IEndpointRouteBuilder MapRemote(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifier/remote").RequireAuthorization();

        // ICT → ask a PC. Unattended PCs (Admin flag) skip the prompt.
        g.MapPost("/sessions", async (RemoteStartRequest req, HttpContext http, IPopDbContext db, ILiveKitTokenService lk,
            IHubContext<IPop.Api.Notifications.NotificationHub> hub, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            if (!await CanHelpAsync(db, me.Value.Id, http.User.IsInRole("Admin"), ct)) return Results.Forbid();
            if (!lk.IsConfigured) return Results.Json(new { message = "Remote help needs LiveKit, which isn't set up on this server." }, statusCode: 503);
            var device = await db.NotifierDevices.Include(d => d.User).FirstOrDefaultAsync(d => d.Id == req.DeviceId, ct);
            if (device is null) return Results.NotFound(new { message = "That computer isn't known." });
            if (device.LastSeenAt < DateTime.UtcNow - NotifierDevice.OnlineWindow) return Results.Json(new { message = $"{device.MachineName} is offline." }, statusCode: 409);
            if (device.UserId == me.Value.Id) return Results.BadRequest(new { message = "That's your own computer." });

            var now = DateTime.UtcNow;
            // Someone is already helping this PC: the newcomer joins that room to watch (view only, still asks the user).
            var running = await db.RemoteSessions.AsNoTracking()
                .Where(x => x.DeviceId == device.Id && x.Status == "active" && x.StartedAt > now - TokenLifetime)
                .OrderByDescending(x => x.StartedAt).Select(x => x.Room).FirstOrDefaultAsync(ct);
            var id = Guid.NewGuid();
            var session = new RemoteSession
            {
                Id = id, HelperId = me.Value.Id, TargetUserId = device.UserId, DeviceId = device.Id, Room = running ?? RoomName(id),
                Mode = running is not null || req.Mode == "view" ? "view" : "control", Unattended = device.Unattended && running is null,
                RequestedAt = now,
            };
            session.Status = session.Unattended ? "active" : "asking";
            session.StartedAt = session.Unattended ? now : null;
            db.RemoteSessions.Add(session);
            await db.SaveChangesAsync(ct);
            await hub.Clients.User(device.UserId.ToString()).SendAsync("remoteRequest", new
            {
                sessionId = session.Id, deviceKey = device.DeviceKey, helper = me.Value.Name, helperInitials = Initials(me.Value.Name),
                mode = session.Mode, unattended = session.Unattended, joining = running is not null, expiresAt = now + AskTimeout,
            }, ct);
            return Results.Ok(new { sessionId = session.Id, status = session.Status, machine = device.MachineName, user = device.User.Name });
        });

        // User's PC answers the prompt.
        g.MapPost("/sessions/{id:guid}/answer", async (Guid id, RemoteAnswerRequest req, HttpContext http, IPopDbContext db,
            IHubContext<IPop.Api.Notifications.NotificationHub> hub, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var s = await db.RemoteSessions.FirstOrDefaultAsync(x => x.Id == id && x.TargetUserId == me.Value.Id, ct);
            if (s is null) return Results.NotFound();
            var status = Answer(s, req.Allow, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            await hub.Clients.User(s.HelperId.ToString()).SendAsync("remoteAnswer", new { sessionId = s.Id, status }, ct);
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
            return Results.Ok(new { url = lk.ServerUrl, token, room = s.Room, role = isHost ? "host" : "helper", mode = s.Mode,
                helper = await db.Users.AsNoTracking().Where(u => u.Id == s.HelperId).Select(u => u.Name).FirstAsync(ct) });
        });

        g.MapPost("/sessions/{id:guid}/end", async (Guid id, RemoteEndRequest req, HttpContext http, IPopDbContext db,
            IHubContext<IPop.Api.Notifications.NotificationHub> hub, CancellationToken ct) =>
        {
            var me = await Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var s = await db.RemoteSessions.FirstOrDefaultAsync(x => x.Id == id && (x.HelperId == me.Value.Id || x.TargetUserId == me.Value.Id), ct);
            if (s is null) return Results.NotFound();
            End(s, s.HelperId == me.Value.Id ? "helper" : "user", req.Files, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            var other = s.HelperId == me.Value.Id ? s.TargetUserId : s.HelperId;
            await hub.Clients.User(other.ToString()).SendAsync("remoteEnded", new { sessionId = s.Id, endedBy = s.EndedBy }, ct);
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
                    x.Unattended, x.RequestedAt, x.StartedAt, x.EndedAt, x.EndedBy, x.Files })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // Admin only: connect without the prompt (kiosk, server). Logged as a session-less audit line.
        g.MapPut("/devices/{id:guid}/unattended", async (Guid id, RemoteUnattendedRequest req, HttpContext http, IPopDbContext db,
            ILogger<RemoteSession> log, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("Admin")) return Results.Forbid();
            var d = await db.NotifierDevices.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();
            d.Unattended = req.Unattended;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Remote help: {Admin} set unattended={Value} on {Machine}.", http.User.FindFirstValue("nik"), req.Unattended, d.MachineName);
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

    /// "active" when allowed in time, "declined" otherwise, "expired" after the 30 s prompt. Idempotent once decided.
    public static string Answer(RemoteSession s, bool allow, DateTime now)
    {
        if (s.Status != "asking") return s.Status;
        if (now > s.RequestedAt + AskTimeout + TimeSpan.FromSeconds(5)) { s.Status = "expired"; s.EndedAt = now; s.EndedBy = "timeout"; return s.Status; }
        if (allow) { s.Status = "active"; s.StartedAt = now; }
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

    private static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    private static async Task<(Guid Id, string Name)?> Me(HttpContext http, IPopDbContext db, CancellationToken ct)
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
