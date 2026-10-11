namespace IPop.Core.Entities;

public enum MeetingRecordingStatus { Recording = 0, Processing = 1, Ready = 2, Failed = 3, Deleted = 4 }

/// A server (LiveKit Egress) recording of a meeting room. The file lives on the NAS; only Viewers
/// (people in the room while it recorded) and admins may see it.
public class MeetingRecording
{
    public Guid Id { get; set; }
    public required string RoomKey { get; set; }
    public required string Title { get; set; }
    public required string EgressId { get; set; }
    public Guid StartedById { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public MeetingRecordingStatus Status { get; set; }
    /// Path the Egress container writes to, relative to MeetingRooms:SpoolDir.
    public string? SpoolPath { get; set; }
    public string? NasPath { get; set; }
    public long? SizeBytes { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Error { get; set; }
    public List<MeetingRecordingViewer> Viewers { get; set; } = [];
}

public class MeetingRecordingViewer
{
    public Guid RecordingId { get; set; }
    public Guid UserId { get; set; }
}
