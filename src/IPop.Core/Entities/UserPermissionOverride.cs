namespace IPop.Core.Entities;

public enum PermissionEffect
{
    Allow,
    Deny
}

public class UserPermissionOverride
{
    public Guid UserId { get; set; }
    public Guid PermissionId { get; set; }
    public PermissionEffect Effect { get; set; }
    /// Empty means global scope; non-empty values scope the override to a Department code.
    public string DepartmentCode { get; set; } = "";
    public User User { get; set; } = default!;
    public Permission Permission { get; set; } = default!;
}
