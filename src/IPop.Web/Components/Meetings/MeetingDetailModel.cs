using IPop.Application.Hall;
using IPop.Web.Models;
using IPop.Web.Pages;

namespace IPop.Web.Components.Meetings;

/// One person on the detail's People list. Key is opaque (photo/colour lookup only).
public record MeetingDetailPerson(string Key, string Name, string Rsvp, bool IsMe, bool IsOrganizer, bool Required = true);

/// What <see cref="MeetingDetail"/> shows, built from a Calls MeetingDto (<see cref="FromMeeting"/>) or a Calendar
/// event detail (<see cref="FromEvent"/>), so both screens render the same panel (meeting scheduling v3 §5/§6).
public sealed record MeetingDetailModel
{
    public required Guid MeetingId { get; init; }
    public Guid? EventId { get; init; }
    public required string Title { get; init; }
    /// UTC.
    public required DateTime StartAt { get; init; }
    public required DateTime EndAt { get; init; }
    /// voice | room | inperson | null (plain event).
    public string? Kind { get; init; }
    public string? RoomKey { get; init; }
    public string? Location { get; init; }
    public bool Cancelled { get; init; }
    public string? OrganizerKey { get; init; }
    public required string OrganizerName { get; init; }
    public bool OrganizerIsMe { get; init; }
    /// Organizer, or a delegate who may edit (Calendar CanEdit): Edit, Cancel and Change room.
    public bool CanManage { get; init; }
    public string? Agenda { get; init; }
    public int? ReminderMinutes { get; init; }
    public string? RepeatText { get; init; }
    /// LiveKit room name of a voice meeting ("meeting-{id:N}"), for /meet/{name}.
    public string? JoinRoomName { get; init; }
    /// My answer (accept | maybe | decline | pending), null when I'm not invited.
    public string? MyResponse { get; init; }
    public IReadOnlyList<MeetingDetailPerson> People { get; init; } = [];
    /// Past meetings: how many were there (null = unknown) and the recordings I may watch.
    public int? Attended { get; init; }
    public IReadOnlyList<RecordingChipInfo> Recordings { get; init; } = [];

    public string? RoomName => MeetingKinds.RoomName(RoomKey);

    /// The password box (CalendarRoomPassword) is asked for only on a room meeting that is not cancelled, and only by
    /// its organizer or a non-declined invitee — the API returns the password to nobody else.
    public bool ShowsPassword => Kind == MeetingKinds.Room && RoomName is not null && !Cancelled
        && (OrganizerIsMe || MyResponse is { } r && r != "decline");

    /// Where Join goes: room → the Meeting Rooms tab join (same as CalendarRoomJoin), voice → /meet/{room}.
    public string? JoinHref => Kind switch
    {
        MeetingKinds.Room when RoomKey is not null && RoomName is not null => $"/calls?tab=rooms&join={RoomKey}",
        MeetingKinds.Voice when JoinRoomName is not null => $"/meet/{JoinRoomName}",
        _ => null,
    };

    public static MeetingDetailModel FromMeeting(MeetingInfo m, Guid? me)
    {
        var kind = CallsSchedule.KindOf(m.Kind, m.Location, m.Attendees.Count > 0);
        var mine = m.Attendees.FirstOrDefault(a => a.UserId == me);
        var organizerIsMe = me is not null && m.OrganizerId == me;
        return new MeetingDetailModel
        {
            MeetingId = m.Id,
            Title = m.Title,
            StartAt = m.StartAt,
            EndAt = m.EndAt,
            Kind = kind,
            RoomKey = kind == MeetingKinds.Room ? m.RoomKey ?? HallBooking.RoomKey(m.Location) : null,
            Location = m.Location,
            Cancelled = m.Status == "cancelled",
            OrganizerKey = m.OrganizerNik,
            OrganizerName = m.OrganizerName,
            OrganizerIsMe = organizerIsMe,
            CanManage = organizerIsMe,
            Agenda = m.Agenda,
            ReminderMinutes = m.ReminderMinutes,
            JoinRoomName = m.RoomName,
            MyResponse = organizerIsMe ? "accept" : mine?.RsvpStatus,
            People = Order(m.Attendees.Select(a => new MeetingDetailPerson(a.Nik, a.Name, a.RsvpStatus, a.UserId == me,
                a.UserId == m.OrganizerId, a.Required))),
        };
    }

    /// From the Calendar event drawer (meeting items only: MeetingId must be set). OwnerName/OwnerNik = the organizer
    /// (a meeting's event row belongs to its organizer); CanEdit covers edit delegates.
    public static MeetingDetailModel FromEvent(IPop.Web.Pages.Calendar.EventDetail e, Guid? me)
    {
        var kind = CallsSchedule.KindOf(e.MeetingKind, e.Location, e.Attendees.Count > 0);
        var ownerId = e.Attendees.FirstOrDefault(a => a.Nik == e.OwnerNik)?.UserId;
        var organizerIsMe = me is not null && ownerId == me;
        var mine = e.Attendees.FirstOrDefault(a => a.UserId == me);
        var meetingId = e.MeetingId ?? Guid.Empty;
        return new MeetingDetailModel
        {
            MeetingId = meetingId,
            EventId = e.Id,
            Title = e.Title,
            StartAt = e.StartAt,
            EndAt = e.EndAt,
            Kind = kind,
            RoomKey = kind == MeetingKinds.Room ? e.RoomKey ?? HallBooking.RoomKey(e.Location) : null,
            Location = e.Location,
            Cancelled = e.MeetingStatus == "cancelled",
            OrganizerKey = e.OwnerNik,
            OrganizerName = e.OwnerName,
            OrganizerIsMe = organizerIsMe,
            CanManage = organizerIsMe || e.CanEdit,
            Agenda = e.Notes,
            ReminderMinutes = e.ReminderMinutes,
            RepeatText = e.RecurrenceText,
            JoinRoomName = e.MeetingId is { } id ? $"meeting-{id:N}" : null,
            MyResponse = organizerIsMe ? "accept" : mine?.Rsvp,
            People = Order(e.Attendees.Select(a => new MeetingDetailPerson(a.Nik, a.Name, a.Rsvp, a.UserId == me, a.Nik == e.OwnerNik))),
        };
    }

    /// Organizer first, then by name.
    private static List<MeetingDetailPerson> Order(IEnumerable<MeetingDetailPerson> people) =>
        people.OrderByDescending(p => p.IsOrganizer).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// Going = accept + maybe (and the organizer); Declined = decline; the rest have not answered.
    public static string Group(MeetingDetailPerson p) =>
        p.IsOrganizer || p.Rsvp is "accept" or "maybe" ? "going" : p.Rsvp == "decline" ? "declined" : "none";
}
