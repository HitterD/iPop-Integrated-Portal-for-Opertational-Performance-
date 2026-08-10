namespace IPop.Core.Entities;

/// Many-to-many between User and Role. The HRIS sync never touches this table —
/// portal authorization is portal business, not HRIS business.
public class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }

    public User User { get; set; } = default!;
    public Role Role { get; set; } = default!;
}
