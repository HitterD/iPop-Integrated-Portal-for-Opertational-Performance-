namespace IPop.Application.MeetingRooms;

public static class MeetingRoomKeys
{
    public static string LiveKitRoom(string key) => $"hall-{key}";
    public static string? KeyOf(string liveKitRoom) => liveKitRoom.StartsWith("hall-", StringComparison.Ordinal) ? liveKitRoom[5..] : null;
    public static readonly string[] AudioSources = ["microphone", "screen_share", "screen_share_audio"];
    public static readonly string[] VideoSources = ["microphone", "camera", "screen_share", "screen_share_audio"];
    /// LiveKit hard cap: 20 people + 1 hidden Egress recorder.
    public const int LiveKitMaxParticipants = 21;
}

public record MeetingRoomPerson(Guid UserId, string Name, bool Sharing, bool RecordingToPc);
public record MeetingRoomBookingDto(Guid MeetingId, string Title, DateTime StartAt, DateTime EndAt, string? OrganizerName);
public record MeetingRoomRecordingState(Guid Id, string StartedByName, DateTime StartedAt);
public record MeetingRoomDto(string Key, string Name, int Capacity, bool VideoAllowed, bool Available,
    IReadOnlyList<MeetingRoomPerson> People, MeetingRoomBookingDto? Now, MeetingRoomBookingDto? Next, MeetingRoomRecordingState? Recording);
public record MeetingRoomJoinDto(string Key, string Name, string LiveKitRoom, string Url, string Token, bool VideoAllowed, string Identity);
public record MeetingRoomJoinResult(MeetingRoomJoinDto? Join, string? Error, IReadOnlyList<string> Suggestions);

public interface IMeetingRoomService
{
    Task<IReadOnlyList<MeetingRoomDto>> ListAsync(CancellationToken ct);
    /// Errors: "unknown_room", "media_unavailable", "room_full" (Suggestions = keys of rooms with space).
    Task<MeetingRoomJoinResult> JoinAsync(Guid userId, string key, CancellationToken ct);
    Task<IReadOnlyDictionary<string, bool>> VideoAsync(CancellationToken ct);
    /// Saves the switch and, when turned off, stops every camera in that room now.
    Task SetVideoAsync(string key, bool allowed, string? actorNik, CancellationToken ct);
}

public record MeetingRecordingDto(Guid Id, string RoomKey, string RoomName, string Title, string StartedByName, DateTime StartedAt,
    int? DurationSeconds, long? SizeBytes, int PeopleCount, string Status, DateTime DeleteAfter);
public record MeetingRecordingResult(MeetingRecordingDto? Recording, string? Error);
public interface IMeetingRecordingService
{
    /// Errors: "unknown_room", "not_in_room", "already_recording", "egress_unavailable".
    Task<MeetingRecordingResult> StartAsync(Guid userId, string key, CancellationToken ct);
    /// Anyone in the room may stop it. Errors: "not_found", "not_in_room".
    Task<MeetingRecordingResult> StopAsync(Guid userId, Guid recordingId, CancellationToken ct);
    /// Ready/Processing recordings the user may see (viewers), or all when admin.
    Task<IReadOnlyList<MeetingRecordingDto>> ListAsync(Guid userId, bool admin, CancellationToken ct);
    Task<bool> CanViewAsync(Guid userId, bool admin, Guid recordingId, CancellationToken ct);
    /// Adds a viewer if a server recording is running in that room (called on every join).
    Task NoteJoinAsync(Guid userId, string key, CancellationToken ct);
    Task<bool> DeleteAsync(Guid recordingId, CancellationToken ct);
}

