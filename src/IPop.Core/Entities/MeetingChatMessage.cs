namespace IPop.Core.Entities;

/// A line of in-meeting chat in a hall room (meeting rooms v2 §3). Kept for the booking's lifetime; deleted by the
/// booking worker an hour after the booking ended and the room emptied — unless it was sent while a server recording ran
/// (<see cref="RecordingId"/>): then it lives as long as that recording.
public class MeetingChatMessage
{
    public Guid Id { get; set; }
    /// The booking (Meeting) running in the room when it was sent.
    public Guid MeetingId { get; set; }
    public required string RoomKey { get; set; }
    /// Guid.Empty for system lines.
    public Guid UserId { get; set; }
    /// "text" | "system" (recording started/stopped).
    public required string Kind { get; set; }
    public required string Text { get; set; }
    public DateTime SentAt { get; set; }
    /// The server recording running when it was sent, with the second of the video it belongs to.
    public Guid? RecordingId { get; set; }
    public int? OffsetSeconds { get; set; }
}
