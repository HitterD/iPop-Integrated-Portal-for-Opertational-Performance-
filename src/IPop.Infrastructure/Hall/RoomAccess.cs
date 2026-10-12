using IPop.Application.Hall;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Hall;

/// A hall-room booking as RoomAccess sees it. <see cref="Password"/> is for server-side checks only — never put it in a
/// DTO for someone who is not the organizer or a non-declined attendee.
public record Booking(Guid MeetingId, string Title, DateTime StartAt, DateTime EndAt, Guid OrganizerId, string? Password);

/// Who may use a room now. Role: "organizer" | "invited" | "guest" (entered the password before) | "outsider" | "free"
/// (no booking holds the room: nobody may join, "Use now" may book it).
public record RoomAccessInfo(Booking? Current, Booking? Next, string Role);

/// What the caller knows about people in the room: <see cref="Occupied"/> now (LiveKit, without EG_ recorders) and
/// <see cref="LastEmptyAtUtc"/>, the last moment the room was seen empty (RoomOccupancyTracker; app start when unknown).
public readonly record struct RoomOccupancy(bool Occupied, DateTime LastEmptyAtUtc)
{
    /// Nobody inside.
    public static RoomOccupancy Empty => new(false, DateTime.MaxValue);
    /// Occupied and never seen empty (tests, and "occupied since forever").
    public static RoomOccupancy Continuous => new(true, DateTime.MinValue);
}

/// The one place that decides room access (meeting rooms v2 §1). A booking's lock window is
/// [StartAt − 10 min, max(EndAt, last moment the room still had people)). Admin has no bypass.
public static class RoomAccess
{
    public static readonly TimeSpan Early = TimeSpan.FromMinutes(10);
    /// A booking never holds the room longer than this after its EndAt, however long people stay.
    public static readonly TimeSpan MaxOverrun = TimeSpan.FromHours(4);
    private static readonly TimeZoneInfo Office = OfficeZone();

    /// Current = the latest active booking whose window has started and which ends after today's local midnight
    /// (Asia/Jakarta — never yesterday's), if it has not ended — or it overruns: the room is occupied now, has not been
    /// seen empty since its EndAt (a reconnect into an emptied room does not revive it), EndAt was less than
    /// <see cref="MaxOverrun"/> ago, and no later booking's window has opened since (a cancelled one included). Next = the earliest booking whose window has not started yet.
    public static async Task<RoomAccessInfo> ForAsync(IPopDbContext db, string roomKey, Guid userId, DateTime nowUtc, RoomOccupancy occupancy,
        CancellationToken ct)
    {
        var (current, next) = await BookingsAsync(db, roomKey, nowUtc, occupancy, ct);
        if (current is null) return new(null, next, "free");
        return new(current, next, await RoleAsync(db, current, userId, ct));
    }

    /// The room's Current and Next booking (same rules as <see cref="ForAsync"/>) without a viewer — for the booking
    /// worker and the room metadata, which then ask <see cref="RoleAsync"/> per person.
    public static async Task<(Booking? Current, Booking? Next)> BookingsAsync(IPopDbContext db, string roomKey, DateTime nowUtc,
        RoomOccupancy occupancy, CancellationToken ct)
    {
        var name = HallBooking.Rooms.FirstOrDefault(r => r.Key == roomKey).Name;
        if (name is null) return (null, null);
        var loc = HallBooking.Location(name);
        var windowOpen = nowUtc + Early;   // StartAt − 10 min ≤ now  ⇔  StartAt ≤ now + 10 min
        var dayStart = DayStartUtc(nowUtc);
        var bookings = db.Meetings.AsNoTracking().Where(m => m.Status != MeetingStatus.Cancelled && m.Location == loc);

        var latest = await bookings.Where(m => m.StartAt <= windowOpen && m.EndAt > dayStart).OrderByDescending(m => m.StartAt)
            .Select(m => new Booking(m.Id, m.Title, m.StartAt, m.EndAt, m.OrganizerId, m.RoomPassword)).FirstOrDefaultAsync(ct);
        var current = latest is not null && nowUtc < latest.EndAt ? latest : null;
        // An overrun belongs only to the last booking that took the room: once a later booking's window opened — even
        // if that booking was cancelled since — the earlier one cannot come back (people stay → they are moved out).
        if (current is null && latest is not null && Overruns(latest, nowUtc, occupancy))
        {
            var (id, start) = (latest.MeetingId, latest.StartAt);
            if (!await db.Meetings.AsNoTracking().AnyAsync(m => m.Location == loc && m.Id != id && m.StartAt > start && m.StartAt <= windowOpen, ct))
                current = latest;
        }
        var next = await bookings.Where(m => m.StartAt > windowOpen).OrderBy(m => m.StartAt)
            .Select(m => new Booking(m.Id, m.Title, m.StartAt, m.EndAt, m.OrganizerId, m.RoomPassword)).FirstOrDefaultAsync(ct);
        return (current, next);
    }

    /// After its EndAt a booking keeps the room only while people have stayed in it without a break, for at most
    /// <see cref="MaxOverrun"/>.
    public static bool Overruns(Booking b, DateTime nowUtc, RoomOccupancy occupancy) =>
        nowUtc >= b.EndAt && occupancy.Occupied && occupancy.LastEmptyAtUtc < b.EndAt && nowUtc < b.EndAt + MaxOverrun;

    /// Today's local (office, Asia/Jakarta) midnight in UTC.
    public static DateTime DayStartUtc(DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), Office);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified), Office);
    }

    private static TimeZoneInfo OfficeZone()
    {
        foreach (var id in new[] { "Asia/Jakarta", "SE Asia Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    }

    /// When <paramref name="b"/> takes the room: its StartAt − 10 min. People of the booking before it who are not
    /// allowed in this one are moved out at that moment.
    public static DateTime WindowStart(Booking b) => b.StartAt - Early;

    /// "organizer" | "invited" | "guest" | "outsider" for <paramref name="userId"/> in booking <paramref name="b"/>.
    public static async Task<string> RoleAsync(IPopDbContext db, Booking b, Guid userId, CancellationToken ct)
    {
        if (b.OrganizerId == userId) return "organizer";
        if (await db.MeetingAttendees.AsNoTracking().AnyAsync(a => a.MeetingId == b.MeetingId && a.UserId == userId && a.RsvpStatus != "decline", ct))
            return "invited";
        if (await db.MeetingRoomGuests.AsNoTracking().AnyAsync(g => g.MeetingId == b.MeetingId && g.UserId == userId, ct))
            return "guest";
        return "outsider";
    }

    public static bool MayEnter(string role) => role is "organizer" or "invited" or "guest";
}
