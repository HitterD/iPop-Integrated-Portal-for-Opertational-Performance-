using System.Security.Claims;
using IPop.Api.Notifier;
using IPop.Application.Notifier;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Per-PC Admin settings in Devices (all optional; only the given ones change).
public record RemoteMachineSettingsRequest(string? Alias = null, string? Group = null, bool? AlwaysAsk = null,
    string? UnattendedPermissions = null, string? AllowedPermissions = null, bool? Excluded = null);

/// Remote help settings and history (BIGIS #8 v3, mockup ipop-remote-v3):
///   GET  /api/notifier/remote/policy                  — everyone who may use Remote: the company policy (read)
///   PUT  /api/notifier/remote/policy                  — Admin: save it (normalised, audited)
///   PUT  /api/notifier/remote/machines/{id}/settings  — Admin: alias, group, always ask, permissions, exclude (audited)
///   POST /api/notifier/remote/machines/{id}/unlock    — Admin: clear a password lock (audited)
///   GET  /api/notifier/remote/history                 — Admin: everyone's; ICT: their own. Sessions + Admin changes.
public static class RemoteAdminEndpoints
{
    public static IEndpointRouteBuilder MapRemoteAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifier/remote").RequireAuthorization();

        g.MapGet("/policy", async (HttpContext http, IPopDbContext db, ISettings settings, CancellationToken ct) =>
        {
            var me = await RemoteEndpoints.Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var isAdmin = http.User.IsInRole("Admin");
            if (!await NotifierEndpoints.IsIctOrAdminAsync(db, me.Value.Id, isAdmin, ct)) return Results.Forbid();
            var p = await RemotePolicyStore.GetAsync(settings, ct);
            // People list only for Admins (who may use Remote: ICT departments + Admin role).
            var people = isAdmin ? await PeopleAsync(db, p, ct) : null;
            return Results.Ok(new { policy = p, people, isAdmin });
        });

        g.MapPut("/policy", async (RemotePolicy req, HttpContext http, IPopDbContext db, ISettings settings, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("Admin")) return Results.Forbid();
            var p = req.Normalize();
            await RemotePolicyStore.SaveAsync(settings, p, http.User.FindFirstValue("nik"), ct);
            return Results.Ok(p);
        });

        g.MapPut("/machines/{id:guid}/settings", async (Guid id, RemoteMachineSettingsRequest req, HttpContext http, IPopDbContext db,
            IHubContext<IPop.Api.Notifications.NotificationHub> hub, IPop.Infrastructure.Calls.ILiveKitRoomAdmin liveKit, ILogger<RemoteMachine> log, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("Admin")) return Results.Forbid();
            var me = await RemoteEndpoints.Me(http, db, ct);
            var m = await db.RemoteMachines.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (m is null || me is null) return Results.NotFound();
            var changes = Apply(m, req);
            var now = DateTime.UtcNow;
            foreach (var c in changes) RemoteAudit.Add(db, m.Id, me.Value.Id, c, now);
            // Excluding a PC or narrowing what unattended may do ends running unattended sessions there.
            var revoke = req.Excluded == true || req.UnattendedPermissions is not null || req.AllowedPermissions is not null;
            var revoked = revoke && changes.Count > 0 ? await RemoteAccess.RevokeAsync(db, m.Id, now, ct) : [];
            await db.SaveChangesAsync(ct);
            await RemoteMachineEndpoints.TellRevokedAsync(hub, liveKit, log, revoked, ct);
            return Results.Ok(new { m.Alias, m.Group, m.AlwaysAsk, m.UnattendedPermissions, m.AllowedPermissions, m.Excluded, changes });
        });

        g.MapPost("/machines/{id:guid}/unlock", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("Admin")) return Results.Forbid();
            var me = await RemoteEndpoints.Me(http, db, ct);
            var m = await db.RemoteMachines.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (m is null || me is null) return Results.NotFound();
            m.LockedUntil = null; m.FailedCount = 0;
            RemoteAudit.Add(db, m.Id, me.Value.Id, "Unlocked after wrong passwords", DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/history", async (string? result, Guid? person, string? q, bool? csv, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await RemoteEndpoints.Me(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var isAdmin = http.User.IsInRole("Admin");
            if (!await NotifierEndpoints.IsIctOrAdminAsync(db, me.Value.Id, isAdmin, ct)) return Results.Forbid();
            var rows = await RemoteHistory.ListAsync(db, isAdmin ? person : me.Value.Id, isAdmin, result, q, ct);
            return csv == true ? Results.File(RemoteHistory.Csv(rows), "text/csv", "remote-sessions.csv") : Results.Ok(rows);
        });

        return app;
    }

    /// Validates and applies the request; returns the human lines for the audit (empty = nothing changed).
    public static List<string> Apply(RemoteMachine m, RemoteMachineSettingsRequest r)
    {
        var out_ = new List<string>();
        if (r.Alias is not null)
        {
            var a = r.Alias.Trim(); a = a.Length > 40 ? a[..40] : a;
            var v = a.Length == 0 ? null : a;
            if (v != m.Alias) { m.Alias = v; out_.Add(v is null ? "Display name cleared" : $"Display name set to “{v}”"); }
        }
        if (r.Group is not null)
        {
            var gname = r.Group.Trim(); gname = gname.Length > 60 ? gname[..60] : gname;
            var v = gname.Length == 0 ? null : gname;
            if (v != m.Group) { out_.Add($"Group changed: {m.Group ?? "—"} → {v ?? "—"}"); m.Group = v; }
        }
        if (r.AlwaysAsk is { } ask && ask != m.AlwaysAsk) { m.AlwaysAsk = ask; out_.Add(ask ? "“Always ask” switched on" : "“Always ask” switched off"); }
        if (r.UnattendedPermissions is not null)
        {
            var v = RemotePerms.Parse(r.UnattendedPermissions).List;
            if (v != (m.UnattendedPermissions ?? RemotePerms.All.List)) { m.UnattendedPermissions = v; out_.Add("Unattended permissions changed"); }
        }
        if (r.AllowedPermissions is not null)
        {
            var v = RemotePerms.Parse(r.AllowedPermissions).List;
            if (v != (m.AllowedPermissions ?? RemotePerms.All.List)) { m.AllowedPermissions = v; out_.Add("Allowed actions changed"); }
        }
        if (r.Excluded is { } ex && ex != m.Excluded) { m.Excluded = ex; out_.Add(ex ? "Excluded from remote help" : "No longer excluded from remote help"); }
        return out_;
    }

    /// ICT/Admin people for "Who can use Remote": everyone with remote rights today, with the policy switch.
    private static async Task<List<object>> PeopleAsync(IPopDbContext db, RemotePolicy p, CancellationToken ct)
    {
        var blocked = await db.RemoteHelperBlocks.AsNoTracking().Select(b => b.UserId).ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive)
            .Select(u => new { u.Id, u.Name, Admin = u.UserRoles.Any(r => r.Role.Name == "Admin") }).ToListAsync(ct);
        var rows = new List<object>();
        foreach (var u in users)
            if (await NotifierEndpoints.IsIctOrAdminAsync(db, u.Id, u.Admin, ct))
                rows.Add(new { u.Id, u.Name, role = u.Admin ? "Admin" : "ICT", allowed = p.Allows(u.Id) && !blocked.Contains(u.Id) });
        return rows;
    }
}
