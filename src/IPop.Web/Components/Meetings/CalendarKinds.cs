using IPop.Application.Hall;
using IPop.Web.Pages;

namespace IPop.Web.Components.Meetings;

/// Meeting scheduling v3 §6: the Calendar side of meeting kinds — what the New event form saves for each kind tile, the
/// kind an edited event opens on, the ?kind=&amp;room= prefill from the booking sheet, and the colour/icon of feed items.
/// Form kind "" = Event (a plain time on the calendar; it still becomes a meeting when people are invited).
public static class CalendarKinds
{
    public const string Event = "";

    /// (Location, Link, Kind) the form posts. Voice → the online place, no link; room → the hall place; in person → the
    /// typed place; Event → the typed place and no kind (the server infers one only when people are invited).
    public static (string? Location, string? Link, string? Kind) SavePlace(string kind, string? roomKey, string? location, string? link)
    {
        string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return kind switch
        {
            MeetingKinds.Voice => (CallsSchedule.OnlineLocation, null, MeetingKinds.Voice),
            MeetingKinds.Room => (MeetingKinds.RoomName(roomKey) is { } n ? HallBooking.Location(n) : null, null, MeetingKinds.Room),
            MeetingKinds.InPerson => (Clean(location), Clean(link), MeetingKinds.InPerson),
            _ => (Clean(location), Clean(link), null),
        };
    }

    /// Why the form can't save yet for this kind; null = fine.
    public static string? Validate(string kind, string? roomKey, string? location) => kind switch
    {
        MeetingKinds.Room when MeetingKinds.RoomName(roomKey) is null => "Choose a meeting room.",
        MeetingKinds.InPerson when string.IsNullOrWhiteSpace(location) => "Where is it? Add a place.",
        _ => null,
    };

    /// The tile an existing event opens on: its stored kind, else inferred from the place (rows from before v3).
    /// A personal event (not a meeting) is always Event.
    public static (string Kind, string? RoomKey) FormKind(string? meetingKind, string? roomKey, string? location, bool hasPeople, bool isMeeting = true)
    {
        if (!isMeeting) return (Event, null);
        var k = CallsSchedule.KindOf(meetingKind, location, hasPeople);
        return k switch
        {
            MeetingKinds.Room => (k, roomKey ?? HallBooking.RoomKey(location)),
            MeetingKinds.Voice or MeetingKinds.InPerson => (k, null),
            _ => (Event, null),
        };
    }

    /// The booking sheet's "More options…" (/calendar?new=1&amp;kind=room&amp;room=kalosi) and Schedule → Voice call
    /// (&amp;kind=voice). Unknown kinds or rooms are ignored.
    public static (string? Kind, string? RoomKey) FromQuery(string? kind, string? room) => kind switch
    {
        MeetingKinds.Room when MeetingKinds.RoomName(room) is not null => (MeetingKinds.Room, room),
        MeetingKinds.Voice or MeetingKinds.InPerson => (kind, null),
        _ => (null, null),
    };

    // ---------------------------------------------------------------- date/time pills (v3 gaps, mockup screen 6)

    /// Dates the Starts pill offers: a new event from today; a new room booking up to 60 days ahead; an edit anything
    /// (an existing meeting may already have started).
    public static (DateTime? Min, DateTime? Max) StartDateRange(bool isNew, string kind, DateTime today) =>
        !isNew ? (null, null)
        : kind == MeetingKinds.Room ? (today.Date, today.Date.AddDays(IPop.Application.MeetingRooms.RoomBookingRules.MaxDaysAhead))
        : (today.Date, null);

    /// A new event's start today is not before this minute (local times; the same rule as the booking sheet).
    public static bool StartTimeDisabled(bool isNew, DateTime startDate, TimeOnly t, DateTime nowLocal) =>
        isNew && startDate.Date == nowLocal.Date && startDate.Date.Add(t.ToTimeSpan()) < new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, nowLocal.Minute, 0);

    /// An end slot on <paramref name="endDate"/>: after the start; a room booking also the same day (00:00 = the end of
    /// it) and at most 8 h long (local times).
    public static bool EndTimeDisabled(string kind, DateTime startLocal, DateTime endDate, TimeOnly t)
    {
        var e = endDate.Date.Add(t.ToTimeSpan());
        if (kind == MeetingKinds.Room && t == TimeOnly.MinValue && endDate.Date == startLocal.Date) e = endDate.Date.AddDays(1);
        if (e <= startLocal) return true;
        if (kind != MeetingKinds.Room) return false;
        var sameDay = e.Date == startLocal.Date || e == startLocal.Date.AddDays(1);
        return !sameDay || e - startLocal > TimeSpan.FromMinutes(IPop.Application.MeetingRooms.RoomBookingRules.MaxMinutes);
    }

    /// Meeting items (mine or invited; not someone else's busy block) get the kind colour; others keep their source colour.
    public static string ItemClass(IPop.Web.Pages.Calendar.FeedItem i) =>
        IsKindItem(i) ? "mk-ev " + MeetingKinds.CssClass(i.MeetingKind, i.RoomKey) : "";

    /// The kind icon for meeting items, else null (the page's source icon).
    public static string? ItemIcon(IPop.Web.Pages.Calendar.FeedItem i) => IsKindItem(i) ? MeetingKinds.Icon(i.MeetingKind) : null;

    /// The room name under a room meeting's title on the week grid.
    public static string? RoomLine(IPop.Web.Pages.Calendar.FeedItem i) =>
        IsKindItem(i) && i.MeetingKind == MeetingKinds.Room ? MeetingKinds.RoomName(i.RoomKey) : null;

    private static bool IsKindItem(IPop.Web.Pages.Calendar.FeedItem i) =>
        i.OwnerNik is null && i.Kind == "meeting" && MeetingKinds.IsKnown(i.MeetingKind);
}
