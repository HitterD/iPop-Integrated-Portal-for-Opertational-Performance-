using IPop.Application.Rbac;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Rbac;

/// Seeds the permission presets. Runs on every startup: creates missing roles and adds missing
/// grants, never removes a grant an admin added later.
public static class RolePresets
{
    public const string HrNews = RoleNames.HrNews;
    public const string HcApproval = RoleNames.HcApproval;
    public const string HcApprovalDirectory = RoleNames.HcApprovalDirectory;
    public const string HcFull = RoleNames.HcFull;

    private static readonly string[] News = ["news.read", "news.write", "news.publish"];
    private static readonly string[] Approval = [PermissionCodes.ApprovalRead, PermissionCodes.ApprovalManage];

    public static readonly IReadOnlyDictionary<string, string[]> Grants = new Dictionary<string, string[]>
    {
        [HrNews] = News,
        [HcApproval] = Approval,
        [HcApprovalDirectory] = [.. Approval, PermissionCodes.DirectoryManage],
        [HcFull] = [.. Approval, PermissionCodes.DirectoryManage, .. News],
    };

    public static string Label(string role) => RoleNames.Label(role);

    public static async Task EnsureAsync(IPopDbContext db, CancellationToken ct)
    {
        var perms = await db.Permissions.ToDictionaryAsync(p => p.Code, p => p.Id, ct);
        foreach (var (name, codes) in Grants)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == name, ct);
            if (role is null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = name };
                db.Roles.Add(role);
                await db.SaveChangesAsync(ct);
            }
            var has = await db.RolePermissions.Where(rp => rp.RoleId == role.Id && rp.DepartmentCode == "")
                .Select(rp => rp.PermissionId).ToListAsync(ct);
            foreach (var code in codes)
                if (perms.TryGetValue(code, out var pid) && !has.Contains(pid))
                    db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = pid });
        }
        await db.SaveChangesAsync(ct);
    }
}
