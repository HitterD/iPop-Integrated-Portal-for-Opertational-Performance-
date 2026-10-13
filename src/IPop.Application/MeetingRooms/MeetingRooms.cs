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
    /// Books a free room from this minute until <paramref name="endUtc"/> through the normal meeting create path (v3 limits:
    /// <see cref="RoomBookingRules"/>; never into the next booking's lock window).
    /// Errors: "unknown_room", a <see cref="RoomBookingRules"/> code ("too_short", "too_long", "different_day", "in_past",
    /// "too_far"), "room_booked" (MaxMinutes = minutes until the next booking's lock window when that is at least 15, else null).
    Task<(Guid? MeetingId, string? Error, int? MaxMinutes)> UseNowAsync(Guid userId, string key, DateTime endUtc, string? title,
        IReadOnlyList<string> attendeeNiks, CancellationToken ct);
    /// v2 clients: <paramref name="minutes"/> from the start (same rules as the end-time form).
    Task<(Guid? MeetingId, string? Error, int? MaxMinutes)> UseNowAsync(Guid userId, string key, int minutes, string? title,
        IReadOnlyList<string> attendeeNiks, CancellationToken ct);
    /// The room's bookings on <paramref name="date"/> (the viewer's local day), titles per the privacy rule. Null = unknown room.
    Task<RoomDayDto?> DayAsync(Guid viewerId, string key, DateOnly date, CancellationToken ct);
    /// All five rooms, free or not for [from, to). <paramref name="exceptMeetingId"/>: the meeting being edited does not count.
    Task<IReadOnlyList<RoomFreeDto>> FreeAtAsync(Guid viewerId, DateTime fromUtc, DateTime toUtc, Guid? exceptMeetingId, CancellationToken ct);
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

/// One booking on a room's day timeline (v3 §2). <paramref name="Visible"/>: the viewer organizes it or is a non-declined
/// attendee; otherwise Title is "Booked" and MeetingId is null (same privacy rule as the room cards).
/// <paramref name="Mine"/>: the viewer organizes it.
public record RoomBlockDto(Guid? MeetingId, string Title, DateTime StartAt, DateTime EndAt, bool Mine, bool Visible);
/// A room's bookings on one local day. WindowStart/WindowEnd (UTC) = 07:00–19:00 local, widened to whole hours around any
/// booking outside it (never beyond the day).
public record RoomDayDto(string Key, string Name, int Capacity, DateOnly Date, DateTime WindowStart, DateTime WindowEnd,
    IReadOnlyList<RoomBlockDto> Blocks);
/// Whether a room is free for a time range. BusyTitle: null when free, else the clashing booking's title for the viewer
/// ("Booked" when they may not see it).
public record RoomFreeDto(string Key, string Name, int Capacity, bool Free, string? BusyTitle);
public record BookingLimits(int MinMinutes, int MaxMinutes, int MaxDaysAhead);

/// v3 booking limits for room meetings (server enforced; the 15-minute snap is UI only).
public static class RoomBookingRules
{
    public const int MinMinutes = 15, MaxMinutes = 8 * 60, MaxDaysAhead = 60;
    public static readonly BookingLimits Limits = new(MinMinutes, MaxMinutes, MaxDaysAhead);
    public const string TooShort = "too_short", TooLong = "too_long", DifferentDay = "different_day", InPast = "in_past", TooFar = "too_far";
    /// Day timeline default window, local time.
    public static readonly TimeSpan DayFrom = TimeSpan.FromHours(7), DayTo = TimeSpan.FromHours(19);

    /// Null when a room booking [start, end) is allowed at <paramref name="nowUtc"/>, else one of the codes above (checked in
    /// that order). Same local day: an end at exactly the next local midnight still counts as the same day. Start not in the
    /// past = not before this minute (Use now starts at the floored minute). <paramref name="keepsStart"/>: an edit that keeps
    /// the booking's existing start (it may have started already, or lie beyond 60 days in a series): only the length and
    /// same-day rules apply.
    public static string? Validate(DateTime startUtc, DateTime endUtc, DateTime nowUtc, TimeZoneInfo tz, bool keepsStart = false)
    {
        var length = endUtc - startUtc;
        if (length < TimeSpan.FromMinutes(MinMinutes)) return TooShort;
        if (length > TimeSpan.FromMinutes(MaxMinutes)) return TooLong;
        var s = Local(startUtc, tz);
        var e = Local(endUtc, tz);
        if (e.Date != s.Date && e != s.Date.AddDays(1)) return DifferentDay;
        var now = Utc(nowUtc);
        var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (keepsStart) return null;
        if (Utc(startUtc) < minute) return InPast;
        if (DateOnly.FromDateTime(s) > DateOnly.FromDateTime(Local(now, tz)).AddDays(MaxDaysAhead)) return TooFar;
        return null;
    }

    /// User-facing text for a Validate code.
    public static string Message(string? code) => code switch
    {
        TooShort => $"A room booking is at least {MinMinutes} minutes.",
        TooLong => "A room booking is at most 8 hours.",
        DifferentDay => "A room booking must start and end on the same day.",
        InPast => "That time has already passed. Pick a time from now on.",
        TooFar => $"Rooms can be booked up to {MaxDaysAhead} days ahead.",
        _ => "Choose a valid time for the room.",
    };

    /// The earliest [s, s + length) at or after <paramref name="from"/> that overlaps none of <paramref name="busy"/> and ends
    /// by <paramref name="dayEnd"/>; null when there is none (the "Try HH:MM–HH:MM" hint).
    public static (DateTime S, DateTime E)? NextFree(IReadOnlyList<(DateTime S, DateTime E)> busy, DateTime from, TimeSpan length, DateTime dayEnd)
    {
        if (length <= TimeSpan.Zero) return null;
        var s = from;
        while (true)
        {
            if (s + length > dayEnd) return null;
            var e = s + length;
            var clash = busy.Where(b => b.S < e && s < b.E).Select(b => (DateTime?)b.E).Max();
            if (clash is null) return (s, e);
            s = clash.Value;
        }
    }

    /// The local day [00:00, 24:00) of <paramref name="date"/> in UTC.
    public static (DateTime Start, DateTime End) DayUtc(DateOnly date, TimeZoneInfo tz) =>
        (ToUtc(date.ToDateTime(TimeOnly.MinValue), tz), ToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), tz));

    /// The timeline window of a day: 07:00–19:00 local, widened to whole hours to include every block, within the day.
    public static (DateTime Start, DateTime End) Window(DateOnly date, TimeZoneInfo tz, IEnumerable<(DateTime S, DateTime E)> blocks)
    {
        var (dayStart, dayEnd) = DayUtc(date, tz);
        var from = ToUtc(date.ToDateTime(TimeOnly.MinValue) + DayFrom, tz);
        var to = ToUtc(date.ToDateTime(TimeOnly.MinValue) + DayTo, tz);
        foreach (var (bs, be) in blocks)
        {
            var s = bs < dayStart ? dayStart : Utc(bs);
            var e = be > dayEnd ? dayEnd : Utc(be);
            if (s < from) from = FloorHour(s, tz);
            if (e > to) to = CeilHour(e, tz);
        }
        return (from < dayStart ? dayStart : from, to > dayEnd ? dayEnd : to);
    }

    /// The user's time zone, falling back to the office zone (Asia/Jakarta, WIB).
    public static TimeZoneInfo ResolveZone(string? id)
    {
        foreach (var candidate in new[] { id, "SE Asia Standard Time", "Asia/Jakarta" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    }

    public static DateTime Local(DateTime utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTimeFromUtc(Utc(utc), tz);
    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
    private static DateTime FloorHour(DateTime utc, TimeZoneInfo tz)
    {
        var l = Local(utc, tz);
        return ToUtc(new DateTime(l.Year, l.Month, l.Day, l.Hour, 0, 0), tz);
    }
    private static DateTime CeilHour(DateTime utc, TimeZoneInfo tz)
    {
        var l = Local(utc, tz);
        var floor = new DateTime(l.Year, l.Month, l.Day, l.Hour, 0, 0);
        return ToUtc(floor == l ? floor : floor.AddHours(1), tz);
    }
}
