using IPop.Application.Rbac;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Rbac;

public sealed class PermissionEvaluator(IPopDbContext db) : IPermissionEvaluator
{
    public async Task<bool> IsAllowedAsync(Guid userId, string permissionCode,
        string? departmentCode, CancellationToken ct = default)
    {
        var permission = await db.Permissions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Code == permissionCode, ct);
        if (permission is null) return false;

        var overrides = await db.UserPermissionOverrides.AsNoTracking()
            .Where(x => x.UserId == userId && x.PermissionId == permission.Id &&
                (x.DepartmentCode == "" || x.DepartmentCode == departmentCode))
            .ToListAsync(ct);
        if (overrides.Any(x => x.Effect == Core.Entities.PermissionEffect.Deny)) return false;
        if (overrides.Any(x => x.Effect == Core.Entities.PermissionEffect.Allow)) return true;

        var roleIds = await db.UserRoles.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => x.RoleId).ToListAsync(ct);
        if (roleIds.Count == 0)
        {
            var defaultRoleId = await db.Roles.AsNoTracking()
                .Where(r => r.Name == "Employee")
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(ct);
            if (defaultRoleId is not null)
                roleIds.Add(defaultRoleId.Value);
        }
        var visited = new HashSet<Guid>();
        while (roleIds.Count > 0)
        {
            var roles = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id))
                .Select(r => new { r.Id, r.ParentRoleId }).ToListAsync(ct);
            foreach (var role in roles)
            {
                if (!visited.Add(role.Id)) continue;
                var allowed = await db.RolePermissions.AsNoTracking().AnyAsync(x =>
                    x.RoleId == role.Id && x.PermissionId == permission.Id &&
                    (x.DepartmentCode == "" || x.DepartmentCode == departmentCode), ct);
                if (allowed) return true;
            }
            roleIds = roles.Where(r => r.ParentRoleId is not null)
                .Select(r => r.ParentRoleId!.Value)
                .Where(id => !visited.Contains(id))
                .Distinct()
                .ToList();
        }
        return false;
    }
}
