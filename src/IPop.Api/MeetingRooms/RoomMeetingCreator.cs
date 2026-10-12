using IPop.Api.Endpoints;
using IPop.Application.MeetingRooms;
using IPop.Infrastructure.Persistence;

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
}
