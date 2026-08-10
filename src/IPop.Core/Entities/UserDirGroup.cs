namespace IPop.Core.Entities;

public class UserDirGroup
{
    public Guid UserId { get; set; }
    public Guid DirGroupId { get; set; }
    public User User { get; set; } = null!;
    public DirGroup DirGroup { get; set; } = null!;
}
