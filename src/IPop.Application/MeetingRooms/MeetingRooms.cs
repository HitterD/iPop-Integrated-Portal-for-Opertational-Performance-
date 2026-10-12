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

/// CanDelete: whether the requesting user may delete it (started it, organized the booking, or Admin).
/// ChatCount: in-meeting chat messages saved with the recording.
public record MeetingRecordingDto(Guid Id, string RoomKey, string RoomName, string Title, string StartedByName, DateTime StartedAt,
    int? DurationSeconds, long? SizeBytes, int PeopleCount, string Status, DateTime DeleteAfter, bool CanDelete = false, int ChatCount = 0);
public record MeetingRecordingResult(MeetingRecordingDto? Recording, string? Error);
public interface IMeetingRecordingService
{
    /// Errors: "unknown_room", "not_in_room", "already_recording", "egress_unavailable".
    Task<MeetingRecordingResult> StartAsync(Guid userId, string key, CancellationToken ct);
    /// Anyone in the room may stop it. Errors: "not_found", "not_in_room".
    Task<MeetingRecordingResult> StopAsync(Guid userId, Guid recordingId, CancellationToken ct);
    /// Ready/Processing recordings the user was present for (viewers only; Admins are not merged in).
    /// <paramref name="admin"/> only decides CanDelete.
    Task<IReadOnlyList<MeetingRecordingDto>> ListAsync(Guid userId, bool admin, CancellationToken ct);
    /// Every Ready/Processing/Failed recording, for Administration → Meeting rooms (audit/delete).
    Task<IReadOnlyList<MeetingRecordingDto>> ListAllAsync(CancellationToken ct);
    /// Started it, organized the booking it was recorded in, or Admin.
    Task<bool> CanDeleteAsync(Guid userId, bool admin, Guid recordingId, CancellationToken ct);
    Task<bool> CanViewAsync(Guid userId, bool admin, Guid recordingId, CancellationToken ct);
    /// Adds a viewer if a server recording is running in that room (called on every join).
    Task NoteJoinAsync(Guid userId, string key, CancellationToken ct);
    /// Marks it Deleted and removes the NAS file right away (a NAS failure leaves NasPath for the nightly purge).
    /// Errors: "not_found" (gone, or the caller may neither see nor delete it), "forbidden", "recording" (still running).
    Task<string?> DeleteAsync(Guid userId, bool admin, Guid recordingId, CancellationToken ct);
}


/// One in-meeting chat line. <paramref name="Kind"/>: "text" | "system" (recording started/stopped; UserId = Guid.Empty).
/// <paramref name="OffsetSeconds"/>: second of the server recording it was sent during (null without one).
public record MeetingChatMessageDto(Guid Id, Guid UserId, string Name, string Text, DateTime SentAt, string Kind, int? OffsetSeconds);
public record MeetingChatResult(MeetingChatMessageDto? Message, string? Error);

public static class MeetingChat
{
    public const int MaxLength = 2000;
    /// LiveKit data topic the API relays chat lines on.
    public const string Topic = "ipop.chat";
    public const string KindText = "text", KindSystem = "system";
    /// Room history returned on join.
    public const int HistoryLimit = 500;
}

/// In-meeting chat of a hall room (meeting rooms v2 §3): stored per booking, relayed to the LiveKit room, and saved with
/// a server recording when one runs.
public interface IMeetingChatService
{
    /// The sender must be allowed in the room's current booking (organizer, invited, guest) AND be in the LiveKit room now.
    /// Errors: "empty", "too_long", "unknown_room", "media_unavailable", "not_in_room", "no_booking", "forbidden",
    /// "rate_limited" (10 per 10 s per user). The relay to the room is best effort: the message is stored either way.
    Task<MeetingChatResult> SendAsync(Guid userId, string key, string? text, CancellationToken ct);
    /// History of the current booking, oldest first (max 500). Errors: "unknown_room", "no_booking", "forbidden".
    Task<(IReadOnlyList<MeetingChatMessageDto>? Messages, string? Error)> HistoryAsync(Guid userId, string key, CancellationToken ct);
    /// Lines saved with a recording, by offset. Null when the user may not view the recording (viewers and Admins).
    Task<IReadOnlyList<MeetingChatMessageDto>?> RecordingChatAsync(Guid userId, bool admin, Guid recordingId, CancellationToken ct);
}
