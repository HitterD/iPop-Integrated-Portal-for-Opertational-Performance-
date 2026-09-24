namespace IPop.Core.Entities;

/// <summary>
/// Composite PK: (UserId, BlockedUserId).
/// Represents user UserId blocking BlockedUserId from direct communication.
/// </summary>
public class UserBlock
{
    public Guid UserId { get; set; }
    public Guid BlockedUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = default!;
    public User BlockedUser { get; set; } = default!;
}
