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

/// The one place that decides room access (meeting rooms v2 §1). A booking's lock window is
/// [StartAt − 10 min, max(EndAt, last moment the room still had people)). Admin has no bypass.
public static class RoomAccess
{
    public static readonly TimeSpan Early = TimeSpan.FromMinutes(10);

    /// <paramref name="occupied"/> = the LiveKit room has people now (callers ask LiveKit, without EG_ recorders), which
    /// keeps an overrunning booking current after its EndAt until the next booking's window starts.
    /// Current = the latest active booking whose window has started, if it has not ended (or the room is occupied).
    /// Next = the earliest booking whose window has not started yet.
    public static async Task<RoomAccessInfo> ForAsync(IPopDbContext db, string roomKey, Guid userId, DateTime nowUtc, bool occupied,
        CancellationToken ct)
    {
        var name = HallBooking.Rooms.FirstOrDefault(r => r.Key == roomKey).Name;
        if (name is null) return new(null, null, "free");
        var loc = HallBooking.Location(name);
        var windowOpen = nowUtc + Early;   // StartAt − 10 min ≤ now  ⇔  StartAt ≤ now + 10 min
        var bookings = db.Meetings.AsNoTracking().Where(m => m.Status != MeetingStatus.Cancelled && m.Location == loc);

        var latest = await bookings.Where(m => m.StartAt <= windowOpen).OrderByDescending(m => m.StartAt)
            .Select(m => new Booking(m.Id, m.Title, m.StartAt, m.EndAt, m.OrganizerId, m.RoomPassword)).FirstOrDefaultAsync(ct);
        var current = latest is not null && (nowUtc < latest.EndAt || occupied) ? latest : null;
        var next = await bookings.Where(m => m.StartAt > windowOpen).OrderBy(m => m.StartAt)
            .Select(m => new Booking(m.Id, m.Title, m.StartAt, m.EndAt, m.OrganizerId, m.RoomPassword)).FirstOrDefaultAsync(ct);
        if (current is null) return new(null, next, "free");
        return new(current, next, await RoleAsync(db, current, userId, ct));
    }

    private static async Task<string> RoleAsync(IPopDbContext db, Booking b, Guid userId, CancellationToken ct)
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
