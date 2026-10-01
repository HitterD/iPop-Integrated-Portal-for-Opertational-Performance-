using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class RbacEndpoints
{
    public static IEndpointRouteBuilder MapRbac(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/rbac").RequireAuthorization("AdminOnly");
        g.MapGet("", async (IPopDbContext db, CancellationToken ct) => Results.Ok(new
        {
            roles = await db.Roles.AsNoTracking().Select(r => new { r.Id, r.Name, r.ParentRoleId }).ToListAsync(ct),
            permissions = await db.Permissions.AsNoTracking().Select(p => new { p.Id, p.Code, p.Description }).ToListAsync(ct),
            rolePermissions = await db.RolePermissions.AsNoTracking().Select(x => new { x.RoleId, x.PermissionId, x.DepartmentCode }).ToListAsync(ct),
        }));

        g.MapPost("/roles", async (RoleRequest req, IPopDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || await db.Roles.AnyAsync(r => r.Name == req.Name.Trim(), ct))
                return Results.BadRequest(new { code = "invalid_role" });
            var role = new Role { Id = Guid.NewGuid(), Name = req.Name.Trim(), ParentRoleId = req.ParentRoleId };
            if (await CreatesCycle(role, db, ct)) return Results.BadRequest(new { code = "role_cycle" });
            if (await InheritsAdmin(role, db, ct)) return Results.BadRequest(new { code = "admin_not_inheritable" });
            db.Roles.Add(role); await db.SaveChangesAsync(ct); return Results.Created($"/api/admin/rbac/roles/{role.Id}", role);
        });

        g.MapPatch("/roles/{id:guid}", async (Guid id, RoleRequest req, IPopDbContext db, CancellationToken ct) =>
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (role is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(req.Name)) role.Name = req.Name.Trim();
            role.ParentRoleId = req.ParentRoleId;
            if (await CreatesCycle(role, db, ct)) return Results.BadRequest(new { code = "role_cycle" });
            if (await InheritsAdmin(role, db, ct)) return Results.BadRequest(new { code = "admin_not_inheritable" });
            await db.SaveChangesAsync(ct); return Results.Ok(role);
        });

        g.MapPut("/roles/{id:guid}/permissions", async (Guid id, PermissionRequest req, IPopDbContext db, CancellationToken ct) =>
        {
            if (!await db.Roles.AnyAsync(r => r.Id == id, ct)) return Results.NotFound();
            var ids = req.PermissionIds.Distinct().ToArray();
            if (await db.Permissions.CountAsync(p => ids.Contains(p.Id), ct) != ids.Length) return Results.BadRequest(new { code = "unknown_permission" });
            var old = await db.RolePermissions.Where(x => x.RoleId == id).ToListAsync(ct);
            db.RolePermissions.RemoveRange(old);
            db.RolePermissions.AddRange(ids.Select(permissionId => new RolePermission { RoleId = id, PermissionId = permissionId, DepartmentCode = "" }));
            await db.SaveChangesAsync(ct); return Results.Ok();
        });

        g.MapPut("/users/{nik}/overrides", async (string nik, OverrideRequest req, IPopDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            var ids = req.Items.Select(x => x.PermissionId).Distinct().ToArray();
            if (await db.Permissions.CountAsync(p => ids.Contains(p.Id), ct) != ids.Length) return Results.BadRequest(new { code = "unknown_permission" });
            var old = await db.UserPermissionOverrides.Where(x => x.UserId == user.Id).ToListAsync(ct);
            db.UserPermissionOverrides.RemoveRange(old);
            db.UserPermissionOverrides.AddRange(req.Items.Select(x => new UserPermissionOverride
            {
                UserId = user.Id, PermissionId = x.PermissionId, Effect = x.Effect,
                DepartmentCode = x.DepartmentCode?.Trim() ?? ""
            }));
            await db.SaveChangesAsync(ct); return Results.Ok();
        });

        return app;
    }

    private static async Task<bool> CreatesCycle(Role role, IPopDbContext db, CancellationToken ct)
    {
        var seen = new HashSet<Guid> { role.Id };
        var next = role.ParentRoleId;
        while (next is Guid id)
        {
            if (!seen.Add(id)) return true;
            next = await db.Roles.Where(r => r.Id == id).Select(r => r.ParentRoleId).SingleOrDefaultAsync(ct);
        }
        return false;
    }

    /// Admin grants every permission; a role under it would silently make its members admins
    /// (pentest V-01). The evaluator ignores such a link anyway; this keeps it out of the data.
    internal static async Task<bool> InheritsAdmin(Role role, IPopDbContext db, CancellationToken ct)
    {
        var seen = new HashSet<Guid> { role.Id };
        var next = role.ParentRoleId;
        while (next is Guid id && seen.Add(id))
        {
            var parent = await db.Roles.Where(r => r.Id == id).Select(r => new { r.Name, r.ParentRoleId }).SingleOrDefaultAsync(ct);
            if (parent is null) return false;
            if (parent.Name == IPop.Infrastructure.Rbac.PermissionEvaluator.AdminRoleName) return true;
            next = parent.ParentRoleId;
        }
        return false;
    }

    public record RoleRequest(string Name, Guid? ParentRoleId);
    public record PermissionRequest(Guid[] PermissionIds);
    public record OverrideRequest(OverrideItem[] Items);
    public record OverrideItem(Guid PermissionId, PermissionEffect Effect, string? DepartmentCode);
}
