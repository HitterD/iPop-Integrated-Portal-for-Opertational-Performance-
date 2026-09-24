using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class MeetingEndpointsTests
{
    [Fact]
    public async Task CancelMeeting_SetsStatusToCancelled()
    {
        using var t = new TestDb();
        var organizer = t.AddUser("ORG1");
        var attendee = t.AddUser("ATT1");

        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            Title = "Standup",
            StartAt = DateTime.UtcNow.AddHours(2),
            EndAt = DateTime.UtcNow.AddHours(3),
            OrganizerId = organizer.Id,
            Status = "aktif",
            CreatedAt = DateTime.UtcNow
        };
        t.Db.Meetings.Add(meeting);
        t.Db.MeetingAttendees.Add(new MeetingAttendee
        {
            Id = Guid.NewGuid(),
            MeetingId = meeting.Id,
            UserId = attendee.Id,
            Required = true,
            RsvpStatus = "pending"
        });
        await t.Db.SaveChangesAsync();

        // Cancel
        meeting.Status = "cancelled";
        await t.Db.SaveChangesAsync();

        await using var v = t.Verify();
        var m = await v.Meetings.SingleAsync(x => x.Id == meeting.Id);
        Assert.Equal("cancelled", m.Status);
    }
}
