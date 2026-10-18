using System.Security.Claims;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public record NotifierDeviceReport(string DeviceKey, string MachineName, string? WindowsUser, string? OsVersion,
    string? AppVersion, IReadOnlyList<string>? LocalIps);

public record NotifierDeviceRow(Guid Id, string MachineName, string? WindowsUser, string? OsVersion, string? AppVersion,
    string? LocalIps, string? RemoteIp, DateTime LastSeenAt, bool Online, string UserName, string? Department, string? Site);

/// iPop Notifier desktop app (docs/notifier/PLAN.md):
///   POST /api/notifier/devices/register  — the app reports this PC (sign-in and every heartbeat)
///   GET  /api/notifier/devices           — ICT/IT department or Admin: every PC for remote help (mockup v2 §3)
///   GET  /api/notifier/me                — who may see the hidden ICT menu
/// Settings live in /api/me/ui-prefs/notifier (UiPrefsEndpoints).
public static class NotifierEndpoints
{
    /// A device counts as online when it reported within this window (the app heartbeats every 60 s).
    public static readonly TimeSpan OnlineWindow = NotifierDevice.OnlineWindow;
    private static readonly string[] IctDepartmentCodes = ["ICT", "ICT-1 Support", "ICT-AIS"];

    public static IEndpointRouteBuilder MapNotifier(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifier").RequireAuthorization();

        g.MapPost("/devices/register", async (NotifierDeviceReport report, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var status = await RegisterAsync(db, userId.Value, report, http.Connection.RemoteIpAddress?.ToString(), DateTime.UtcNow, ct);
            return status == 204 ? Results.NoContent() : Results.BadRequest(new { message = "Invalid device report." });
        });

        g.MapGet("/me", async (HttpContext http, IPopDbContext db, IConfiguration cfg, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var since = DateTime.UtcNow - NotifierDevice.OnlineWindow;
            // My own PCs with the app (computer name only): iPop's notification settings explain the browser pause.
            var pcs = await db.NotifierDevices.AsNoTracking().Where(d => d.UserId == userId && d.LastSeenAt >= since)
                .OrderBy(d => d.MachineName).Select(d => d.MachineName).ToListAsync(ct);
            // Where "Open in iPop" goes: the public web address (prod: https://ipop.santos.co.id), not the API host.
            var webBase = cfg["Notifier:WebBase"] ?? cfg["Oidc:WebBase"];
            return Results.Ok(new { ictTools = await IsIctOrAdminAsync(db, userId.Value, http.User.IsInRole("Admin"), ct), onlinePcs = pcs, webBase });
        });

        g.MapGet("/devices", async (string? q, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await MyId(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (!await IsIctOrAdminAsync(db, userId.Value, http.User.IsInRole("Admin"), ct)) return Results.Forbid();
            return Results.Ok(await ListAsync(db, q, DateTime.UtcNow, ct));
        });

        return app;
    }

    /// 204 saved, 400 invalid. Upserts by (user, device key); trims everything to the column sizes.
    public static async Task<int> RegisterAsync(IPopDbContext db, Guid userId, NotifierDeviceReport r, string? remoteIp, DateTime now, CancellationToken ct)
    {
        var key = (r.DeviceKey ?? "").Trim();
        var machine = (r.MachineName ?? "").Trim();
        if (key.Length is < 8 or > 64 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return 400;
        if (machine.Length is 0 or > 64) return 400;
        var ips = r.LocalIps?.Select(i => i.Trim()).Where(i => System.Net.IPAddress.TryParse(i, out _)).Take(8).ToList() ?? [];
        var row = await db.NotifierDevices.FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceKey == key, ct);
        if (row is null)
        {
            row = new NotifierDevice { Id = Guid.NewGuid(), UserId = userId, DeviceKey = key, MachineName = machine, FirstSeenAt = now };
            db.NotifierDevices.Add(row);
        }
        row.MachineName = machine;
        row.WindowsUser = Cut(r.WindowsUser, 128);
        row.OsVersion = Cut(r.OsVersion, 128);
        row.AppVersion = Cut(r.AppVersion, 32);
        row.LocalIps = ips.Count == 0 ? null : Cut(string.Join(", ", ips), 256);
        row.RemoteIp = Cut(remoteIp, 64);
        row.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        return 204;
    }

    /// The hidden ICT menu: Admin role, or a member of an ICT/IT department (or one of its sub-units).
    public static async Task<bool> IsIctOrAdminAsync(IPopDbContext db, Guid userId, bool isAdmin, CancellationToken ct)
    {
        if (isAdmin) return true;
        var deptId = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DepartmentId).FirstOrDefaultAsync(ct);
        for (var hops = 0; deptId is { } id && hops < 6; hops++)
        {
            var d = await db.Departments.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Code, x.Name, x.ParentId }).FirstOrDefaultAsync(ct);
            if (d is null) return false;
            if (IctDepartmentCodes.Contains(d.Code, StringComparer.OrdinalIgnoreCase) || d.Name.Equals("ICT", StringComparison.OrdinalIgnoreCase)
                || d.Name.Equals("IT", StringComparison.OrdinalIgnoreCase)) return true;
            deptId = d.ParentId;
        }
        return false;
    }

    public static async Task<List<NotifierDeviceRow>> ListAsync(IPopDbContext db, string? q, DateTime now, CancellationToken ct)
    {
        var since = now - OnlineWindow;
        var query = db.NotifierDevices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(d => EF.Functions.ILike(d.MachineName, term) || EF.Functions.ILike(d.User.Name, term)
                || EF.Functions.ILike(d.User.Nik, term) || (d.RemoteIp != null && EF.Functions.ILike(d.RemoteIp, term))
                || (d.LocalIps != null && EF.Functions.ILike(d.LocalIps, term)));
        }
        return await query.OrderByDescending(d => d.LastSeenAt).Take(500)
            .Select(d => new NotifierDeviceRow(d.Id, d.MachineName, d.WindowsUser, d.OsVersion, d.AppVersion, d.LocalIps, d.RemoteIp,
                d.LastSeenAt, d.LastSeenAt >= since, d.User.Name, d.User.Department != null ? d.User.Department.Name : null,
                d.User.Site != null ? d.User.Site.Name : null))
            .ToListAsync(ct);
    }

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();

    private static Task<Guid?> MyId(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirstValue("nik");
        return db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
