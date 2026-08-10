namespace IPop.Core.Entities;

public class RolePermission
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
    /// Empty means global scope; non-empty values scope the permission to a Department code.
    public string DepartmentCode { get; set; } = "";
    public Role Role { get; set; } = default!;
    public Permission Permission { get; set; } = default!;
}
