namespace IPop.Core.Entities;

/// A portal role. ParentRoleId models the PRD §2 dept → sub-dept hierarchy: PermissionEvaluator
/// walks parents, except that Admin's grants are never inherited (only direct members get them).
public class Role
{
    public Guid Id { get; set; }
    public required string Name { get; set; }        // unique — e.g. "Admin", "Employee"
    public Guid? ParentRoleId { get; set; }

    public Role? Parent { get; set; }
    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}
