using IPop.Application.Hall;
using IPop.Application.MeetingRooms;

namespace IPop.Web.Components.Meetings;

/// Meeting scheduling v3 §1: one colour, icon and label per meeting kind, used by the badge, the detail header,
/// Calendar chips and the legend. Room kind = the room's own roast colour (r-{key}); voice = indigo; in person = teal;
/// no kind = a plain event (blue).
public static class MeetingKinds
{
    public const string Voice = "voice", Room = "room", InPerson = "inperson";

    public static bool IsKnown(string? kind) => kind is Voice or Room or InPerson;

    /// Who holds a clashing date: "Booked by “title”", or "Already booked" when the title is hidden from the viewer
    /// (the server sends the placeholder "Booked").
    public static string BookedByText(string? bookedBy) =>
        string.IsNullOrWhiteSpace(bookedBy) || bookedBy == "Booked" ? "Already booked" : $"Booked by “{bookedBy}”";

    /// Room name for a hall key ("kapal-api" → "Kapal Api"), null for an unknown key.
    public static string? RoomName(string? roomKey) =>
        roomKey is null ? null : HallBooking.Rooms.FirstOrDefault(r => r.Key == roomKey).Name;

    /// Colour class: r-{key} for a known room (an unknown room falls back to the voice colour rather than failing),
    /// k-voice, k-person, or k-event for a plain event.
    public static string CssClass(string? kind, string? roomKey) => kind switch
    {
        Room => RoomName(roomKey) is not null ? "r-" + roomKey : "k-voice",
        Voice => "k-voice",
        InPerson => "k-person",
        _ => "k-event",
    };

    /// Lucide icon name.
    public static string Icon(string? kind) => kind switch
    {
        Room => "video",
        Voice => "headphones",
        InPerson => "map-pin",
        _ => "calendar",
    };

    /// Badge label: the room name, "Voice call", "In person" ("Event" for no kind).
    public static string Label(string? kind, string? roomKey, string? roomName = null) => kind switch
    {
        Room => roomName ?? RoomName(roomKey) ?? "Meeting room",
        Voice => "Voice call",
        InPerson => "In person",
        _ => "Event",
    };

    /// Detail header caption: "Meeting room · Kalosi", "Voice call", "In person".
    public static string Caption(string? kind, string? roomKey) => kind switch
    {
        Room => RoomName(roomKey) is { } n ? $"Meeting room · {n}" : "Meeting room",
        _ => Label(kind, roomKey),
    };

    /// "40 min", "2 h", "1 h 30 min".
    public static string Duration(TimeSpan d)
    {
        var m = (int)Math.Round(d.TotalMinutes);
        if (m < 60) return $"{m} min";
        return m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60} min";
    }

    /// "Today, Tuesday 13 October", "Wednesday 14 October", "Friday 8 January 2027" (English, culture independent).
    public static string DateLabel(DateTime local, DateTime today)
    {
        var d = local.Date;
        var label = $"{DatePickerMath.WeekdayLong[DatePickerMath.MondayIndex(d.DayOfWeek)]} {d.Day} {DatePickerMath.MonthLong[d.Month - 1]}";
        if (d.Year != today.Year) label += $" {d.Year}";
        return d == today.Date ? $"Today, {label}" : label;
    }

    /// "10:00–11:00".
    public static string TimeRange(DateTime localStart, DateTime localEnd) => $"{localStart:HH\\:mm}–{localEnd:HH\\:mm}";

    /// Reminder row: "10 min before", "1 h before", "1 day before".
    public static string ReminderLabel(int minutes) => minutes switch
    {
        < 60 => $"{minutes} min before",
        1440 => "1 day before",
        _ when minutes % 1440 == 0 => $"{minutes / 1440} days before",
        _ when minutes % 60 == 0 => $"{minutes / 60} h before",
        _ => $"{Duration(TimeSpan.FromMinutes(minutes))} before",
    };

    // ---------------------------------------------------------------- room day timeline (v3 §2)

    /// The first booking that overlaps [startUtc, endUtc) (same overlap rule as the server's hall conflict check:
    /// back-to-back is free). Null = it fits.
    public static RoomBlockDto? Clash(IEnumerable<RoomBlockDto>? blocks, DateTime startUtc, DateTime endUtc) =>
        (blocks ?? []).Where(b => Utc(b.StartAt) < Utc(endUtc) && Utc(startUtc) < Utc(b.EndAt)).OrderBy(b => b.StartAt).FirstOrDefault();

    /// "Try HH:MM–HH:MM": the next free slot of the same length after the clashing booking (and not before this minute),
    /// ending by <paramref name="windowEndUtc"/>. Mirrors RoomBookingRules.NextFree. Null = nothing fits that day.
    public static (DateTime Start, DateTime End)? NextSlot(IEnumerable<RoomBlockDto>? blocks, DateTime startUtc, DateTime endUtc,
        DateTime windowEndUtc, DateTime nowUtc)
    {
        var list = (blocks ?? []).Select(b => (S: Utc(b.StartAt), E: Utc(b.EndAt))).ToList();
        if (Clash(blocks, startUtc, endUtc) is not { } c) return (Utc(startUtc), Utc(endUtc));
        var now = Utc(nowUtc);
        var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var from = Utc(c.EndAt) > minute ? Utc(c.EndAt) : minute;
        return RoomBookingRules.NextFree(list, from, Utc(endUtc) - Utc(startUtc), Utc(windowEndUtc));
    }

    public static DateTime Utc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        _ => d.ToUniversalTime(),
    };
}
