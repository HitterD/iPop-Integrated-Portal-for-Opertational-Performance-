namespace IPop.Core.Entities;

/// One-to-one link between a Project and its dedicated chat room (interconnect ipop-21).
/// The ChatRoom is created with Type="group" and Name = project name; members are
/// auto-synced from ProjectMember so every project member can talk in the room.
public class ProjectRoom
{
    /// PK + FK to Project (one project => one room).
    public Guid ProjectId { get; set; }

    /// FK to ChatRoom (unique — one room belongs to at most one project).
    public Guid RoomId { get; set; }

    public DateTime CreatedAt { get; set; }

    public Project Project { get; set; } = default!;
    public ChatRoom Room { get; set; } = default!;
}
