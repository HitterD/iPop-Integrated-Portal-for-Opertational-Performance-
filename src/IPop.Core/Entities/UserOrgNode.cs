namespace IPop.Core.Entities;

public class UserOrgNode
{
    public Guid UserId { get; set; }
    public Guid OrgNodeId { get; set; }
    public User User { get; set; } = null!;
    public OrgNode OrgNode { get; set; } = null!;
}
