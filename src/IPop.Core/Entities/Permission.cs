namespace IPop.Core.Entities;

public class Permission
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public string? Description { get; set; }
    public ICollection<RolePermission> RolePermissions { get; set; } = [];
    public ICollection<UserPermissionOverride> UserOverrides { get; set; } = [];
}
