namespace IPop.Core.Entities;

/// A portal role. ParentRoleId lets the hierarchy from PRD §2 (dept → sub-dept
/// inheritance) be represented now; no endpoint resolves inherited permissions yet.
public class Role
{
    public Guid Id { get; set; }
    public required string Name { get; set; }        // unique — e.g. "Admin", "Employee"
    public Guid? ParentRoleId { get; set; }

    public Role? Parent { get; set; }
    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}
