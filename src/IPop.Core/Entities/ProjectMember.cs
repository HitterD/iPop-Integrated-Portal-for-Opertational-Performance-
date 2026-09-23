namespace IPop.Core.Entities;

/// Project-level membership. Owner is tracked on Project.OwnerId; this table captures additional members with roles.
/// Role: "owner" | "manager" | "member" | "viewer"
public class ProjectMember
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }

    /// Role within the project (distinct from global RBAC).
    public required string Role { get; set; }

    public DateTime JoinedAt { get; set; }

    public Project Project { get; set; } = default!;
    public User User { get; set; } = default!;
}
