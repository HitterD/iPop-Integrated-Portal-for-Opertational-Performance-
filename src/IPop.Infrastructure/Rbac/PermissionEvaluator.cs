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

        var roleIds = await BaseRoleIdsAsync(userId, ct);
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

    /// Everyone is an Employee: that role is the baseline under whatever else they hold (an HC or
    /// HR preset adds to it, it never replaces chat, mail and the rest).
    private async Task<List<Guid>> BaseRoleIdsAsync(Guid userId, CancellationToken ct)
    {
        var roleIds = await db.UserRoles.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => x.RoleId).ToListAsync(ct);
        var employee = await db.Roles.AsNoTracking().Where(r => r.Name == "Employee")
            .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        if (employee is { } e && !roleIds.Contains(e)) roleIds.Add(e);
        return roleIds;
    }

    /// Every permission code the user holds globally, for the client to decide what to show — same
    /// rules as IsAllowedAsync (global scope only, Admin grants not inherited), in a few queries.
    /// The server still checks each endpoint with IsAllowedAsync.
    public async Task<IReadOnlyList<string>> AllowedCodesAsync(Guid userId, CancellationToken ct = default)
    {
        var roleIds = await BaseRoleIdsAsync(userId, ct);
        var direct = roleIds.ToHashSet();
        var roles = await db.Roles.AsNoTracking().Select(r => new { r.Id, r.Name, r.ParentRoleId }).ToDictionaryAsync(r => r.Id, ct);
        var effective = new HashSet<Guid>();
        var queue = new Queue<Guid>(roleIds);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!roles.TryGetValue(id, out var role) || !effective.Add(id)) continue;
            if (role.Name == AdminRoleName && !direct.Contains(id)) { effective.Remove(id); continue; }
            if (role.ParentRoleId is { } parent) queue.Enqueue(parent);
        }
        var granted = (await db.RolePermissions.AsNoTracking()
            .Where(rp => effective.Contains(rp.RoleId) && rp.DepartmentCode == "")
            .Select(rp => rp.Permission.Code).ToListAsync(ct)).ToHashSet();
        var overrides = await db.UserPermissionOverrides.AsNoTracking()
            .Where(o => o.UserId == userId && o.DepartmentCode == "")
            .Select(o => new { o.Permission.Code, o.Effect }).ToListAsync(ct);
        foreach (var o in overrides.Where(o => o.Effect == Core.Entities.PermissionEffect.Allow)) granted.Add(o.Code);
        foreach (var o in overrides.Where(o => o.Effect == Core.Entities.PermissionEffect.Deny)) granted.Remove(o.Code);
        return granted.Order().ToList();
    }
}
