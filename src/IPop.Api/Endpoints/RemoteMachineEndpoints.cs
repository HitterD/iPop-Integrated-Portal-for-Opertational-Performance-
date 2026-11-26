using System.Security.Claims;
using IPop.Api.Notifier;
using IPop.Application.Auth;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public record RemotePasswordRequest(string? Password);
public record RemoteFavouriteRequest(bool On);

/// One row of the Remote window (address book): a physical PC with its remote ID, the person on it now, and online.
public record RemoteBookRow(Guid DeviceId, Guid? MachineId, string MachineName, string? RemoteId, string UserName, string? Ip,
    bool Online, DateTime LastSeenAt, bool Unattended, bool Favourite, DateTime? LastConnectedAt, bool Laptop,
    string? Alias = null, string? Group = null, bool Excluded = false, string? BusyBy = null);

/// BIGIS #8 phase 1a — per-PC remote ID, the Remote window's address book (favourites + recent), and the Admin's
/// unattended password (hash only; never returned).
///   GET  /api/notifier/remote/book                   — ICT/Admin: every PC (one row per remote ID), favourites, recent
///   PUT  /api/notifier/remote/machines/{id}/favourite — my favourite PCs
///   PUT  /api/notifier/remote/machines/{id}/password  — Admin: set (or change) the unattended password
///   DELETE /api/notifier/remote/machines/{id}/password — Admin: remove unattended access
public static class RemoteMachineEndpoints
{
    public const int MaxFavourites = 40; // ui-prefs JSON is capped at 2 KB (40 ids ≈ 1.6 KB)

    public static IEndpointRouteBuilder MapRemoteMachines(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifier/remote").RequireAuthorization();

        g.MapGet("/book", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await RemoteEndpoints.Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            if (!await NotifierEndpoints.IsIctOrAdminAsync(db, me.Value.Id, http.User.IsInRole("Admin"), ct)) return Results.Forbid();
            return Results.Ok(await BookAsync(db, me.Value.Id, DateTime.UtcNow, ct));
        });

        g.MapPut("/machines/{id:guid}/favourite", async (Guid id, RemoteFavouriteRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await RemoteEndpoints.Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            if (!await NotifierEndpoints.IsIctOrAdminAsync(db, me.Value.Id, http.User.IsInRole("Admin"), ct)) return Results.Forbid();
            return Results.StatusCode(await SetFavouriteAsync(db, me.Value.Id, id, req.On, DateTime.UtcNow, ct));
        });

        g.MapPut("/machines/{id:guid}/password", async (Guid id, RemotePasswordRequest req, HttpContext http, IPopDbContext db, IPasswordHasher hasher,
            IPop.Application.Settings.ISettings settings, IHubContext<IPop.Api.Notifications.NotificationHub> hub, IPop.Infrastructure.Calls.ILiveKitRoomAdmin liveKit, ILogger<RemoteMachine> log, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("Admin")) return Results.Forbid();
            var me = await RemoteEndpoints.Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var policy = await RemotePolicyStore.GetAsync(settings, ct);
            if (RemoteAccess.CheckNewPassword(req.Password, policy.MinPassword) is { } why) return Results.BadRequest(new { message = why });
            var m = await db.RemoteMachines.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (m is null) return Results.NotFound();
            var revoked = await RemoteAccess.SetPasswordAsync(db, m, req.Password!, me.Value.Id, hasher, DateTime.UtcNow, ct);
            RemoteAudit.Add(db, m.Id, me.Value.Id, "Unattended password changed", DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            await TellRevokedAsync(hub, liveKit, log, revoked, ct);
            log.LogInformation("Remote help: {Admin} set the unattended password on {Machine} ({RemoteId}); {Count} session(s) revoked.",
                http.User.FindFirstValue("nik"), m.ComputerName, m.ConnectId, revoked.Count);
            return Results.NoContent();
        });

        g.MapDelete("/machines/{id:guid}/password", async (Guid id, HttpContext http, IPopDbContext db,
            IHubContext<IPop.Api.Notifications.NotificationHub> hub, IPop.Infrastructure.Calls.ILiveKitRoomAdmin liveKit, ILogger<RemoteMachine> log, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("Admin")) return Results.Forbid();
            var me = await RemoteEndpoints.Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var m = await db.RemoteMachines.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (m is null) return Results.NotFound();
            var revoked = await RemoteAccess.RemoveAsync(db, m, me.Value.Id, DateTime.UtcNow, ct);
            RemoteAudit.Add(db, m.Id, me.Value.Id, "Unattended access removed", DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            await TellRevokedAsync(hub, liveKit, log, revoked, ct);
            log.LogInformation("Remote help: {Admin} removed unattended access on {Machine} ({RemoteId}).", http.User.FindFirstValue("nik"), m.ComputerName, m.ConnectId);
            return Results.NoContent();
        });

        return app;
    }

    /// Each revoked session ends on both sides: "remoteEnded" over the hub (the apps show why), and LiveKit drops both
    /// participants from the room on the server (review PR #5) — so a client that ignores the hub message, or never
    /// gets it, still loses the stream and the data channel. LiveKit errors are logged, never fatal.
    public static async Task TellRevokedAsync(IHubContext<IPop.Api.Notifications.NotificationHub> hub, IPop.Infrastructure.Calls.ILiveKitRoomAdmin? liveKit,
        ILogger? log, IEnumerable<RemoteSession> revoked, CancellationToken ct)
    {
        foreach (var s in revoked)
        {
            await hub.Clients.Users([s.HelperId.ToString(), s.TargetUserId.ToString()]).SendAsync("remoteEnded", new { sessionId = s.Id, endedBy = "revoked" }, ct);
            if (liveKit is not { IsConfigured: true }) continue;
            foreach (var identity in new[] { "host:" + s.TargetUserId, "helper:" + s.HelperId })
            {
                try { await liveKit.RemoveAsync(s.Room, identity, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log?.LogWarning(ex, "Remote help: couldn't remove {Identity} from {Room}.", identity, s.Room); }
            }
        }
    }

    /// One row per PC (the Notifier seen last on it), newest first; my favourites and the PCs I connected to lately.
    public static async Task<object> BookAsync(IPopDbContext db, Guid me, DateTime now, CancellationToken ct)
    {
        var since = now - NotifierDevice.OnlineWindow;
        var devices = await db.NotifierDevices.AsNoTracking().OrderByDescending(d => d.LastSeenAt).Take(2000)
            .Select(d => new { d.Id, d.MachineId, d.MachineName, d.LastSeenAt, d.LocalIps, d.RemoteIp, d.OsVersion, User = d.User.Name,
                RemoteId = d.Machine != null ? d.Machine.ConnectId : null, Ua = d.Machine != null && d.Machine.Unattended && d.Machine.PasswordHash != null,
                Alias = d.Machine != null ? d.Machine.Alias : null, Group = d.Machine != null && d.Machine.Group != null ? d.Machine.Group : d.User.Department != null ? d.User.Department.Name : null,
                Excluded = d.Machine != null && d.Machine.Excluded })
            .ToListAsync(ct);
        // Who is helping which PC now ("IN SESSION"; Connect then offers Join view-only).
        var alive = now - RemoteEndpoints.AliveWindow;
        var busy = await db.RemoteSessions.AsNoTracking().Where(s => s.Status == "active" && (s.AliveAt ?? s.StartedAt) > alive && s.Device.MachineId != null)
            .Select(s => new { s.Device.MachineId, s.Helper.Name }).ToListAsync(ct);
        var favs = await FavouritesAsync(db, me, ct);
        var recent = await db.RemoteSessions.AsNoTracking().Where(s => s.HelperId == me && s.Device.MachineId != null)
            .GroupBy(s => s.Device.MachineId).Select(x => new { Id = x.Key!.Value, At = x.Max(s => s.RequestedAt) })
            .OrderByDescending(x => x.At).Take(10).ToListAsync(ct);
        var rows = devices.GroupBy(d => d.MachineId ?? d.Id).Select(x => x.First()).Take(500).Select(d => new RemoteBookRow(
            d.Id, d.MachineId, d.MachineName, d.RemoteId, d.User, (d.LocalIps ?? "").Split(',')[0].Trim() is { Length: > 0 } ip ? ip : d.RemoteIp,
            d.LastSeenAt >= since, d.LastSeenAt, d.Ua, d.MachineId is { } m && favs.Contains(m),
            recent.FirstOrDefault(r => r.Id == d.MachineId)?.At, (d.OsVersion ?? "").Contains("Laptop"),
            d.Alias, d.Group, d.Excluded, busy.FirstOrDefault(b => b.MachineId == d.MachineId)?.Name)).ToList();
        return new { rows, recent = recent.Select(r => r.Id), notInstalled = Array.Empty<object>() };
    }

    /// Favourite PCs live in the person's ui-prefs ("remote-favs": machine ids).
    public static async Task<HashSet<Guid>> FavouritesAsync(IPopDbContext db, Guid me, CancellationToken ct)
    {
        var json = await db.UserUiPrefs.AsNoTracking().Where(p => p.UserId == me && p.Key == FavKey).Select(p => p.Json).FirstOrDefaultAsync(ct);
        try { return json is null ? [] : [.. System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(json) ?? []]; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    public const string FavKey = "remote-favs";

    /// 204 saved, 404 unknown PC, 400 over <see cref="MaxFavourites"/>.
    public static async Task<int> SetFavouriteAsync(IPopDbContext db, Guid me, Guid machineId, bool on, DateTime now, CancellationToken ct)
    {
        if (!await db.RemoteMachines.AnyAsync(m => m.Id == machineId, ct)) return 404;
        var favs = await FavouritesAsync(db, me, ct);
        if (on) favs.Add(machineId); else favs.Remove(machineId);
        if (favs.Count > MaxFavourites) return 400;
        var json = System.Text.Json.JsonSerializer.Serialize(favs);
        var row = await db.UserUiPrefs.FirstOrDefaultAsync(p => p.UserId == me && p.Key == FavKey, ct);
        if (row is null) db.UserUiPrefs.Add(row = new UserUiPref { Id = Guid.NewGuid(), UserId = me, Key = FavKey, Json = json });
        else row.Json = json;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return 204;
    }
}
