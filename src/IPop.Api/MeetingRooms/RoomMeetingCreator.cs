using IPop.Api.Endpoints;
using IPop.Application.MeetingRooms;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.MeetingRooms;

/// "Use now" bookings go through the same create path as the meeting form (attendees, calendar row, reminders, room
/// password, double-booking check).
public sealed class RoomMeetingCreator(IPopDbContext db) : IRoomBookingWriter
{
    public async Task<Guid?> CreateAsync(Guid organizerId, string title, DateTime startUtc, DateTime endUtc, string location,
        IReadOnlyList<string> attendeeNiks, CancellationToken ct)
    {
        var (result, id, _) = await MeetingEndpoints.ApplyCreateAsync(db, organizerId,
            new MeetingEndpoints.CreateMeetingRequest(title, startUtc, endUtc, null, location, null, null, null, null, null, attendeeNiks), ct);
        return result == MeetingEndpoints.CreateResult.Created ? id : null;
    }

    public async Task<string?> SetEndAsync(Guid organizerId, Guid meetingId, DateTime endUtc, CancellationToken ct)
    {
        var m = await db.Meetings.AsNoTracking().Where(x => x.Id == meetingId)
            .Select(x => new { x.Title, x.StartAt, x.Location, x.Link, x.Agenda }).FirstOrDefaultAsync(ct);
        if (m is null) return "not_found";
        // AttendeeNiks / ReminderMinutes null = unchanged; answers are kept (an extension is not a new time to answer).
        var (result, _, _) = await MeetingEndpoints.ApplyUpdateAsync(db, meetingId, organizerId,
            new MeetingEndpoints.UpdateMeetingRequest(m.Title, DateTime.SpecifyKind(m.StartAt, DateTimeKind.Utc), endUtc, m.Location, m.Link,
                m.Agenda, null, null), ct, keepAnswers: true);
        return result switch
        {
            MeetingEndpoints.UpdateResult.Applied => null,
            MeetingEndpoints.UpdateResult.NotFound => "not_found",
            MeetingEndpoints.UpdateResult.Forbidden => "forbidden",
            MeetingEndpoints.UpdateResult.Cancelled => "cancelled",
            _ => "room_booked",
        };
    }
}
