using IPop.Api.Rbac;
using IPop.Application.Hris;
using IPop.Core.Presence;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class DirectoryEndpoints
{
    public static IEndpointRouteBuilder MapDirectory(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/directory/departments", async (IPopDbContext db, CancellationToken ct) =>
        {
            var depts = await db.Departments.Include(d => d.Parent).AsNoTracking()
                .Where(d => d.IsActive).ToListAsync(ct);
            var counts = await db.Users.Where(u => u.IsActive && u.DepartmentId != null)
                .GroupBy(u => u.DepartmentId!.Value)
                .Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);
            var byId = counts.ToDictionary(c => c.Id, c => c.N);
            return depts.Select(d => new DirectoryDepartment(
                d.Code, d.Name, d.Parent?.Code, byId.GetValueOrDefault(d.Id)));
        }).RequireAuthorization().RequirePermission("directory.read");

        app.MapGet("/api/directory/sites", async (IPopDbContext db, CancellationToken ct) =>
            await db.Sites.AsNoTracking().Where(s => s.IsActive)
                .OrderBy(s => s.Name).Select(s => new DirectorySite(s.Code, s.Name)).ToListAsync(ct))
            .RequireAuthorization().RequirePermission("directory.read");

        app.MapGet("/api/directory/me", async (IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            var me = await db.Users.Include(u => u.Department).Include(u => u.Site)
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik, ct);
            return me is null
                ? Results.NotFound()
                : Results.Ok(new DirectoryEmployee(me.Nik, me.Name, me.Position, me.Department?.Name,
                    me.Department?.Code, me.Site?.Name, me.Site?.Code, null, me.Email, me.Extension,
                    me.Location, PresenceStatus.ComputeStatus(me.PresenceLastSeenAt,
                        me.PresenceManualStatus, DateTime.UtcNow),
                    me.UserRoles.Select(ur => ur.Role.Name).ToArray(), me.PhotoPath));
        }).RequireAuthorization();

        app.MapGet("/api/directory/employees", async (HttpContext http, string? q, string? departmentCode,
            string? siteCode, string? presence, string? sort, bool? desc, int? page, int? pageSize,
            IPopDbContext db, CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 120, 1, 200);
            var query = db.Users.Include(u => u.Department).Include(u => u.Site)
                .AsNoTracking().Where(u => u.IsActive);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                var pat = $"%{term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
                query = query.Where(u => EF.Functions.ILike(u.Name, pat, @"\") || EF.Functions.ILike(u.Nik, pat, @"\"));
            }
            if (!string.IsNullOrWhiteSpace(departmentCode))
                query = query.Where(u => u.Department != null && u.Department.Code == departmentCode.Trim());
            if (!string.IsNullOrWhiteSpace(siteCode))
                query = query.Where(u => u.Site != null && u.Site.Code == siteCode.Trim());

            var now = DateTime.UtcNow;
            var rows = await query.Select(u => new
            {
                User = u,
                Status = PresenceStatus.ComputeStatus(u.PresenceLastSeenAt, u.PresenceManualStatus, now),
            }).ToListAsync(ct);
            if (!string.IsNullOrWhiteSpace(presence)) rows = rows.Where(x => x.Status == presence.Trim()).ToList();
            var names = rows.ToDictionary(x => x.User.Nik, x => x.User.Name);
            var ordered = (sort?.ToLowerInvariant()) switch
            {
                "nik" => desc == true ? rows.OrderByDescending(x => x.User.Nik) : rows.OrderBy(x => x.User.Nik),
                "status" => desc == true ? rows.OrderByDescending(x => x.Status).ThenBy(x => x.User.Nik) : rows.OrderBy(x => x.Status).ThenBy(x => x.User.Nik),
                _ => desc == true ? rows.OrderByDescending(x => x.User.Name).ThenBy(x => x.User.Nik) : rows.OrderBy(x => x.User.Name).ThenBy(x => x.User.Nik),
            };
            var total = ordered.Count();
            var items = ordered.Skip((p - 1) * size).Take(size).Select(x => new DirectoryEmployee(
                x.User.Nik, x.User.Name, x.User.Position, x.User.Department?.Name, x.User.Department?.Code,
                x.User.Site?.Name, x.User.Site?.Code,
                x.User.ManagerNik is not null && names.TryGetValue(x.User.ManagerNik, out var manager) ? manager : null,
                x.User.Email, x.User.Extension, x.User.Location, x.Status, null, x.User.PhotoPath)).ToArray();
            return Results.Ok(new DirectoryPage(total, p, size, items));
        }).RequireAuthorization().RequirePermission("directory.read");

        // Member picker: admin add-by-name on dir-group / org-node drawers.
        // Public so any directory.read caller can reuse it; admins drive it via the picker.
        app.MapGet("/api/directory/member-search", async (string? q, IPopDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Results.Ok(Array.Empty<MemberSearchRow>());
            var term = q.Trim();
            var pat = $"%{term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
            return Results.Ok(await db.Users.AsNoTracking().Where(u => u.IsActive)
                .Where(u => EF.Functions.ILike(u.Name, pat, @"\") || EF.Functions.ILike(u.Nik, pat, @"\"))
                .OrderBy(u => u.Name).Take(8)
                .Select(u => new MemberSearchRow(u.Nik, u.Name, u.Position, u.Department!.Name))
                .ToListAsync(ct));
        }).RequireAuthorization().RequirePermission("directory.read");

        // Custom directory groups — read-only for everyone, admin edits via /api/admin/dir-groups.
        app.MapGet("/api/directory/dir-groups", async (IPopDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var groups = await db.DirGroups
                .Include(g => g.Members).ThenInclude(m => m.User).ThenInclude(u => u.Department)
                .Include(g => g.Members).ThenInclude(m => m.User).ThenInclude(u => u.Site)
                .Where(g => g.IsActive).OrderBy(g => g.SortOrder).ToListAsync(ct);
            return groups.Select(g => new DirGroupDto(
                g.Id, g.Name, g.ParentId, g.SortOrder, g.IsActive,
                g.Members.Select(m => new DirGroupMemberDto(
                    m.User.Nik, m.User.Name, m.User.Position,
                    m.User.Department?.Name, m.User.Site?.Name,
                    PresenceStatus.ComputeStatus(m.User.PresenceLastSeenAt, m.User.PresenceManualStatus, now)
                )).ToArray()
            )).ToArray();
        }).RequireAuthorization().RequirePermission("directory.read");

        // Org chart — read-only for everyone, admin edits via /api/admin/org-nodes.
        app.MapGet("/api/directory/org-tree", async (IPopDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var nodes = await db.OrgNodes
                .Include(n => n.Members).ThenInclude(m => m.User).ThenInclude(u => u.Department)
                .Include(n => n.Members).ThenInclude(m => m.User).ThenInclude(u => u.Site)
                .Where(n => n.IsActive).OrderBy(n => n.SortOrder).ToListAsync(ct);
            return nodes.Select(n => new OrgNodeDto(
                n.Id, n.Name, n.Title, n.ParentId, n.DepartmentId, n.SortOrder, n.IsActive,
                n.Members.Select(m => new OrgNodeMemberDto(
                    m.User.Nik, m.User.Name, m.User.Position,
                    m.User.Department?.Name, m.User.Site?.Name,
                    PresenceStatus.ComputeStatus(m.User.PresenceLastSeenAt, m.User.PresenceManualStatus, now)
                )).ToArray()
            )).ToArray();
        }).RequireAuthorization().RequirePermission("directory.read");

        return app;
    }

    public record MemberSearchRow(string Nik, string Name, string? Position, string? Department);
}
