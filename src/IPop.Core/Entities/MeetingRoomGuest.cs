namespace IPop.Core.Entities;

/// Someone who is not invited to a hall-room booking but entered its room password once: they may rejoin that
/// booking without it, also after the organizer sets a new password (meeting rooms v2 §1).
public class MeetingRoomGuest
{
    public Guid MeetingId { get; set; }
    public Guid UserId { get; set; }
    public DateTime AdmittedAt { get; set; }
}
