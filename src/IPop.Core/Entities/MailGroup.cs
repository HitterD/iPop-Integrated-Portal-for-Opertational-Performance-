namespace IPop.Core.Entities;

/// A personal recipient group in the mail address book. Visible only to its owner; shared groups
/// are the admin-managed <see cref="DirGroup"/>s.
public class MailGroup
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User Owner { get; set; } = default!;
    public ICollection<MailGroupMember> Members { get; set; } = [];
}

public class MailGroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }

    public MailGroup Group { get; set; } = default!;
    public User User { get; set; } = default!;
}
