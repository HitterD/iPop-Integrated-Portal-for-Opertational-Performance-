namespace IPop.Application.MeetingRooms;

public static class MeetingRoomKeys
{
    public static string LiveKitRoom(string key) => $"hall-{key}";
    public static string? KeyOf(string liveKitRoom) => liveKitRoom.StartsWith("hall-", StringComparison.Ordinal) ? liveKitRoom[5..] : null;
    public static readonly string[] AudioSources = ["microphone", "screen_share", "screen_share_audio"];
    public static readonly string[] VideoSources = ["microphone", "camera", "screen_share", "screen_share_audio"];
    /// LiveKit hard cap: 20 people + 1 hidden Egress recorder.
    public const int LiveKitMaxParticipants = 21;
    /// Meeting-room LiveKit tokens are short-lived so a token cannot be reused to walk back in after the booking
    /// (LiveKit refreshes tokens of connected sessions itself, so a running meeting is not cut off).
    public static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(10);
}

public record MeetingRoomPerson(Guid UserId, string Name, bool Sharing, bool RecordingToPc);
/// <paramref name="IsOrganizer"/>: the viewer organizes this booking (shows Extend).
public record MeetingRoomBookingDto(Guid MeetingId, string Title, DateTime StartAt, DateTime EndAt, string? OrganizerName, bool IsOrganizer = false);
public record MeetingRoomRecordingState(Guid Id, string StartedByName, DateTime StartedAt);
/// <paramref name="Access"/> for the viewer (v2 §1.6): "free" (no booking holds the room: Use now / Book), "invited" (the
/// viewer may walk in: organizer, attendee or admitted guest), "starting" (same, but inside the 10-minute early window:
/// "Starts HH:mm · Join early"), "locked" (someone else's booking: Enter with password).
/// <paramref name="Now"/> = RoomAccess' current booking (also while it overruns an occupied room), with its real StartAt.
public record MeetingRoomDto(string Key, string Name, int Capacity, bool VideoAllowed, bool Available,
    IReadOnlyList<MeetingRoomPerson> People, MeetingRoomBookingDto? Now, MeetingRoomBookingDto? Next, MeetingRoomRecordingState? Recording,
    string Access = MeetingRoomAccess.Free);

public static class MeetingRoomAccess
{
    public const string Free = "free", Invited = "invited", Starting = "starting", Locked = "locked";
}
public record MeetingRoomJoinDto(string Key, string Name, string LiveKitRoom, string Url, string Token, bool VideoAllowed, string Identity,
    int RetentionDays = 90);
public record MeetingRoomJoinResult(MeetingRoomJoinDto? Join, string? Error, IReadOnlyList<string> Suggestions);

public interface IMeetingRoomService
{
    /// Booking titles and organizers only for the viewer's own bookings (organizer or non-declined attendee);
    /// everyone else sees "Booked" with the times.
    Task<IReadOnlyList<MeetingRoomDto>> ListAsync(Guid viewerId, CancellationToken ct);
    /// Only inside a booking (RoomAccess): organizer and non-declined attendees directly, admitted guests directly,
    /// anyone else with the booking's password (then admitted as a guest). Admin has no bypass.
    /// Errors: "unknown_room", "media_unavailable", "no_booking", "password_required", "wrong_password",
    /// "too_many_attempts", "room_full" (Suggestions = keys of rooms with space).
    Task<MeetingRoomJoinResult> JoinAsync(Guid userId, string key, string? password, CancellationToken ct);
    /// Books a free room from this minute for <paramref name="minutes"/> through the normal meeting create path.
    /// Minutes: 30, 60 or 90; a shorter value (≥ 15) only when the next booking caps the room.
    /// Errors: "unknown_room", "invalid_minutes", "room_booked" (MaxMinutes = minutes until the next booking's lock
    /// window when that is at least 15, else null).
    Task<(Guid? MeetingId, string? Error, int? MaxMinutes)> UseNowAsync(Guid userId, string key, int minutes, string? title,
        IReadOnlyList<string> attendeeNiks, CancellationToken ct);
    /// Organizer only: the current booking ends 15 minutes later (through the normal meeting update path).
    /// Errors: "unknown_room", "no_booking" (nothing current), "forbidden" (not the organizer), "room_booked" (the new end
    /// would reach the next booking's lock window, StartAt − 10 min), "cancelled", "media_unavailable" (LiveKit not reachable,
    /// so an overrun cannot be confirmed). Returns the booking and its new EndAt.
    Task<(Guid? MeetingId, DateTime? EndAt, string? Error)> ExtendAsync(Guid userId, string key, CancellationToken ct);
    Task<IReadOnlyDictionary<string, bool>> VideoAsync(CancellationToken ct);
    /// Saves the switch and, when turned off, stops every camera in that room now.
    Task SetVideoAsync(string key, bool allowed, string? actorNik, CancellationToken ct);
}

/// Creates a hall-room booking as an ordinary meeting (attendees, calendar row, password). Implemented in the API on top
/// of MeetingEndpoints.ApplyCreateAsync so "Use now" and the meeting form share one create path.
public interface IRoomBookingWriter
{
    /// Null MeetingId = the room is already booked in that time (the meeting create conflict check).
    Task<Guid?> CreateAsync(Guid organizerId, string title, DateTime startUtc, DateTime endUtc, string location,
        IReadOnlyList<string> attendeeNiks, CancellationToken ct);
    /// Moves the meeting's end (organizer only) through the normal update path, keeping title, place, attendees and
    /// reminders. Errors: "not_found", "forbidden", "cancelled", "room_booked".
    Task<string?> SetEndAsync(Guid organizerId, Guid meetingId, DateTime endUtc, CancellationToken ct);
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

