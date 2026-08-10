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
            string? siteCode, string? divisionCode, string? presence, string? sort, bool? desc, int? page,
            int? pageSize, IPopDbContext db, CancellationToken ct) =>
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
            if (!string.IsNullOrWhiteSpace(divisionCode))
                query = query.Where(u => u.Division != null && u.Division.Code == divisionCode.Trim());

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

        // Structure tree: Site ▸ Department ▸ Division with live member counts, for the
        // Directory master-detail nav. A (Site, Department) pairing only appears if an active
        // Division links them — a Department can thus appear under more than one Site.
        app.MapGet("/api/directory/structure", async (IPopDbContext db, CancellationToken ct) =>
        {
            var divisions = await db.Divisions.Include(d => d.Department).Include(d => d.Site)
                .AsNoTracking().Where(d => d.IsActive
                    && d.Department != null && d.Department.IsActive
                    && d.Site != null && d.Site.IsActive)
                .ToListAsync(ct);
            var counts = await db.Users.Where(u => u.IsActive && u.DivisionId != null)
                .GroupBy(u => u.DivisionId!.Value)
                .Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);
            var byDivision = counts.ToDictionary(c => c.Id, c => c.N);
            var noDivisionCount = await db.Users.CountAsync(u => u.IsActive && u.DivisionId == null, ct);

            return Results.Ok(BuildStructure(divisions, byDivision, noDivisionCount));
        }).RequireAuthorization().RequirePermission("directory.read");

        // Division search: which divisions contain a name/NIK match, with per-division match
        // counts. Same ILIKE-escaping as /employees and /member-search.
        app.MapGet("/api/directory/divisions", async (string? q, IPopDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<DirectoryDivisionMatch>());
            var term = q.Trim();
            var pat = $"%{term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
            var matches = await db.Users.AsNoTracking()
                .Where(u => u.IsActive && u.Division != null && u.Division.IsActive)
                .Where(u => EF.Functions.ILike(u.Name, pat, @"\") || EF.Functions.ILike(u.Nik, pat, @"\"))
                .Select(u => new { u.Division!.Code, u.Division.Name }).ToListAsync(ct);
            return Results.Ok(AggregateDivisionMatches(matches.Select(m => (m.Code, m.Name))));
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

    // Pure aggregation step of /api/directory/structure — split out from the DB query so the
    // Site/Department grouping and count roll-up is unit-testable without EF translation concerns.
    internal static DirectoryStructure BuildStructure(IEnumerable<Core.Entities.Division> activeDivisions,
        IReadOnlyDictionary<Guid, int> memberCountByDivisionId, int noDivisionCount)
    {
        var sites = activeDivisions.GroupBy(d => d.Site!.Code).Select(siteGroup =>
        {
            var depts = siteGroup.GroupBy(d => d.Department!.Code).Select(deptGroup =>
            {
                var divs = deptGroup.Select(d => new DirectoryDivisionNode(
                    d.Code, d.Name, memberCountByDivisionId.GetValueOrDefault(d.Id))).OrderBy(x => x.Name).ToArray();
                return new DirectoryDepartmentNode(deptGroup.Key, deptGroup.First().Department!.Name,
                    divs.Sum(x => x.MemberCount), divs);
            }).OrderBy(x => x.Name).ToArray();
            return new DirectorySiteNode(siteGroup.Key, siteGroup.First().Site!.Name,
                depts.Sum(x => x.MemberCount), depts);
        }).OrderBy(x => x.Name).ToArray();
        return new DirectoryStructure(sites, noDivisionCount);
    }

    // Pure aggregation step of /api/directory/divisions — split out so it's testable without
    // going through EF.Functions.ILike, which is Npgsql-only and doesn't translate on SQLite
    // (the TestDb seam). ponytail: no SQLite-testable ILIKE path exists yet; add one if the
    // search endpoint's SQL filtering itself needs a regression test.
    internal static DirectoryDivisionMatch[] AggregateDivisionMatches(IEnumerable<(string Code, string Name)> matchedUserDivisions) =>
        matchedUserDivisions.GroupBy(x => x.Code)
            .Select(g => new DirectoryDivisionMatch(g.Key, g.First().Name, g.Count()))
            .OrderBy(x => x.Name).ToArray();
}
