using IPop.Application.Hall;
using IPop.Core.Entities;

namespace IPop.Tests;

/// Explicit v2 fixtures: test people present in media are invitees of a running booking, never ad-hoc rooms.
internal static class MeetingRoomTestBookings
{
    public static Guid Ensure(TestDb t, string key, params Guid[] users)
    {
        var location = HallBooking.Location(HallBooking.Rooms.First(r => r.Key == key).Name);
        var m = t.Db.Meetings.FirstOrDefault(x => x.Location == location && x.Status != MeetingStatus.Cancelled);
        if (m is null)
        {
            m = new Meeting { Id = Guid.NewGuid(), OrganizerId = users[0], Title = "Weekly sync", Location = location,
                StartAt = DateTime.UtcNow.AddHours(-2), EndAt = DateTime.UtcNow.AddHours(2), RoomPassword = "123456", CreatedAt = DateTime.UtcNow };
            t.Db.Meetings.Add(m);
        }
        foreach (var uid in users.Distinct())
            if (!t.Db.MeetingAttendees.Any(a => a.MeetingId == m.Id && a.UserId == uid))
                t.Db.MeetingAttendees.Add(new MeetingAttendee { Id = Guid.NewGuid(), MeetingId = m.Id, UserId = uid, RsvpStatus = "accept" });
        t.Db.SaveChanges();
        return m.Id;
    }
}
