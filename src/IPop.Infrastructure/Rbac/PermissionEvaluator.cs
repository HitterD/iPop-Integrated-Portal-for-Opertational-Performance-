using IPop.Application.Rbac;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Rbac;

public sealed class PermissionEvaluator(IPopDbContext db) : IPermissionEvaluator
{
    /// Only direct members get Admin's grants. A role that names Admin as its parent (the
    /// pentest V-01 escalation: Employee → Admin handed every user news.publish, chat.admin…)
    /// stops the walk there instead of inheriting everything.
    public const string AdminRoleName = "Admin";

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
        var direct = roleIds.ToHashSet();
        while (roleIds.Count > 0)
        {
            var roles = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Name, r.ParentRoleId }).ToListAsync(ct);
            foreach (var role in roles)
            {
                if (!visited.Add(role.Id)) continue;
                if (role.Name == AdminRoleName && !direct.Contains(role.Id)) continue;
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

    /// Every permission code the user holds globally (role walk + overrides), for the client to
    /// decide what to show. The server still checks each endpoint with IsAllowedAsync.
    public async Task<IReadOnlyList<string>> AllowedCodesAsync(Guid userId, CancellationToken ct = default)
    {
        var codes = await db.Permissions.AsNoTracking().Select(p => p.Code).ToListAsync(ct);
        var allowed = new List<string>();
        foreach (var code in codes)
            if (await IsAllowedAsync(userId, code, null, ct)) allowed.Add(code);
        return allowed;
    }
}
