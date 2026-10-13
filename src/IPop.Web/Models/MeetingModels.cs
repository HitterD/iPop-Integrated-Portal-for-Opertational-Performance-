namespace IPop.Web.Models;

/// Client mirror of the API's MeetingAttendeeDto. Nik is an opaque key (photos, links); never render it.
public record MeetingAttendeeInfo(Guid UserId, string Name, string Nik, bool Required, string RsvpStatus, string? Note);

/// Client mirror of the API's MeetingDto (GET /api/meetings/upcoming, /api/meetings/{id}). OrganizerNik is an opaque key.
public record MeetingInfo(Guid Id, string Title, DateTime StartAt, DateTime EndAt, string? TimeZone,
    string? Location, string? Link, Guid OrganizerId, string OrganizerName, string OrganizerNik,
    string Status, IReadOnlyList<MeetingAttendeeInfo> Attendees, int AcceptCount, int MaybeCount,
    int DeclineCount, int PendingCount, string MyResponse, string? Agenda = null, string? RoomName = null,
    Guid? RoomId = null, int? ReminderMinutes = null, string? RoomPassword = null, string? Kind = null, string? RoomKey = null);

/// A person for avatar stacks: Key = opaque (NIK) for the photo lookup, never printed.
public record MeetingPersonRef(string Key, string Name);
