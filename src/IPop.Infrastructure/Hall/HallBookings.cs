using IPop.Application.Hall;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Hall;

/// Meeting-room bookings in SJA Hall are meetings located "SJA Hall · {room}" (see <see cref="HallBooking"/>).
public static class HallBookings
{
    /// The meeting holding the room now (organizer/attendee check for <paramref name="userId"/>), or null.
    public static async Task<HallRoomBooking?> CurrentAsync(IPopDbContext db, string roomKey, Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        var name = HallBooking.Rooms.FirstOrDefault(r => r.Key == roomKey).Name;
        if (name is null) return null;
        var loc = HallBooking.Location(name);
        return await db.Meetings.AsNoTracking()
            .Where(m => m.Status != MeetingStatus.Cancelled && m.Location == loc && m.StartAt <= nowUtc && nowUtc < m.EndAt)
            .OrderBy(m => m.StartAt)
            .Select(m => new HallRoomBooking(m.Id, m.Title, m.EndAt,
                m.OrganizerId == userId || m.Attendees.Any(a => a.UserId == userId && a.RsvpStatus != "decline")))
            .FirstOrDefaultAsync(ct);
    }

    /// Another active meeting already holds this hall room in [start, end). Null = free (or not a hall room).
    public static async Task<string?> ConflictAsync(IPopDbContext db, string? location, DateTime startUtc, DateTime endUtc, Guid? exceptMeetingId,
        CancellationToken ct)
    {
        if (HallBooking.RoomKey(location) is null) return null;
        var loc = HallBooking.Normalize(location);
        return await db.Meetings.AsNoTracking()
            .Where(m => m.Status != MeetingStatus.Cancelled && m.Location == loc && m.Id != exceptMeetingId
                && m.StartAt < endUtc && startUtc < m.EndAt)
            .Select(m => m.Title).FirstOrDefaultAsync(ct);
    }

    /// The earliest active meeting of this room that starts after now (null = nothing booked); Allowed for <paramref name="userId"/>.
    public static async Task<HallNextBooking?> NextAsync(IPopDbContext db, string roomKey, Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        var name = HallBooking.Rooms.FirstOrDefault(r => r.Key == roomKey).Name;
        if (name is null) return null;
        var loc = HallBooking.Location(name);
        return await db.Meetings.AsNoTracking()
            .Where(m => m.Status != MeetingStatus.Cancelled && m.Location == loc && m.StartAt > nowUtc)
            .OrderBy(m => m.StartAt)
            .Select(m => new HallNextBooking(m.Id, m.Title, m.StartAt, m.EndAt,
                db.Users.Where(u => u.Id == m.OrganizerId).Select(u => u.Name).FirstOrDefault() ?? "",
                m.OrganizerId == userId || m.Attendees.Any(a => a.UserId == userId && a.RsvpStatus != "decline")))
            .FirstOrDefaultAsync(ct);
    }
}
