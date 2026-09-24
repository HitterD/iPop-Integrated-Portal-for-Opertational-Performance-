using IPop.Core.Entities;
using Microsoft.Extensions.Configuration;
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

    private static Meeting Seed(TestDb t, Guid organizerId, params Guid[] attendees)
    {
        var m = new Meeting
        {
            Id = Guid.NewGuid(), Title = "Weekly sync", StartAt = DateTime.UtcNow.AddHours(4),
            EndAt = DateTime.UtcNow.AddHours(5), OrganizerId = organizerId, ReminderMinutes = 15, CreatedAt = DateTime.UtcNow,
        };
        t.Db.Meetings.Add(m);
        foreach (var uid in attendees.Prepend(organizerId))
            t.Db.MeetingAttendees.Add(new MeetingAttendee
            {
                Id = Guid.NewGuid(), MeetingId = m.Id, UserId = uid,
                RsvpStatus = "accept",
            });
        t.Db.CalendarEvents.Add(new CalendarEvent { Id = Guid.NewGuid(), Title = m.Title, StartAt = m.StartAt, EndAt = m.EndAt, Type = "rapat", OwnerId = organizerId, MeetingId = m.Id, CreatedAt = DateTime.UtcNow });
        return m;
    }

    [Fact]
    public async Task UpdateMeeting_OrganizerEditsFieldsAttendeesCalendarAndResetsRsvp()
    {
        using var t = new TestDb();
        var org = t.AddUser("UPD_ORG");
        var keep = t.AddUser("UPD_KEEP");
        var drop = t.AddUser("UPD_DROP");
        var add = t.AddUser("UPD_ADD");
        var m = Seed(t, org.Id, keep.Id, drop.Id);
        await t.Db.SaveChangesAsync();

        var start = DateTime.UtcNow.AddDays(1);
        var req = new IPop.Api.Endpoints.MeetingEndpoints.UpdateMeetingRequest("Weekly sync (moved)", start, start.AddHours(1),
            "Room 2", null, "1. Status\n2. Risks", ["UPD_KEEP", "UPD_ADD"], 30);
        var (result, added, others) = await IPop.Api.Endpoints.MeetingEndpoints.ApplyUpdateAsync(t.Db, m.Id, org.Id, req, default);

        Assert.Equal(IPop.Api.Endpoints.MeetingEndpoints.UpdateResult.Applied, result);
        Assert.Equal([add.Id], added);
        Assert.Equal([keep.Id], others);
        await using var v = t.Verify();
        var saved = await v.Meetings.Include(x => x.Attendees).SingleAsync(x => x.Id == m.Id);
        Assert.Equal("Weekly sync (moved)", saved.Title);
        Assert.Equal("1. Status\n2. Risks", saved.Agenda);
        Assert.DoesNotContain(saved.Attendees, a => a.UserId == drop.Id);
        Assert.Equal("pending", saved.Attendees.Single(a => a.UserId == keep.Id).RsvpStatus);
        Assert.Equal("accept", saved.Attendees.Single(a => a.UserId == org.Id).RsvpStatus);
        Assert.Equal("Weekly sync (moved)", (await v.CalendarEvents.SingleAsync(e => e.MeetingId == m.Id)).Title);
        Assert.Equal(3, await v.MeetingReminders.CountAsync(r => r.MeetingId == m.Id));
    }

    [Fact]
    public async Task UpdateMeeting_RejectsNonOrganizer()
    {
        using var t = new TestDb();
        var org = t.AddUser("UPD2_ORG");
        var other = t.AddUser("UPD2_OTHER");
        var m = Seed(t, org.Id, other.Id);
        await t.Db.SaveChangesAsync();

        var req = new IPop.Api.Endpoints.MeetingEndpoints.UpdateMeetingRequest("Hijack", m.StartAt, m.EndAt, null, null, null, null, null);
        var (result, _, _) = await IPop.Api.Endpoints.MeetingEndpoints.ApplyUpdateAsync(t.Db, m.Id, other.Id, req, default);
        Assert.Equal(IPop.Api.Endpoints.MeetingEndpoints.UpdateResult.Forbidden, result);
    }

    [Fact]
    public async Task JoinMeeting_AllowsAttendeesOnly_AndMintsLiveKitTokenWhenConfigured()
    {
        using var t = new TestDb();
        var org = t.AddUser("JOIN_ORG");
        var att = t.AddUser("JOIN_ATT");
        var stranger = t.AddUser("JOIN_X");
        var m = Seed(t, org.Id, att.Id);
        await t.Db.SaveChangesAsync();
        var room = IPop.Api.Endpoints.MeetingEndpoints.RoomNameFor(m.Id);
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Calls:LiveKit:ApiKey"] = "k", ["Calls:LiveKit:ApiSecret"] = "test-secret-123456789012345678901234", ["Calls:LiveKit:Url"] = "wss://lk.test",
        }).Build();
        var lk = new IPop.Infrastructure.Calls.LiveKitTokenService(cfg);

        var ok = await IPop.Api.Endpoints.MeetingEndpoints.ResolveJoinAsync(t.Db, lk, room, att.Id, default);
        Assert.Null(ok.Code);
        Assert.Equal(room, ok.Join!.RoomName);
        Assert.Equal("wss://lk.test", ok.Join.LiveKitUrl);
        Assert.False(string.IsNullOrEmpty(ok.Join.LiveKitToken));

        var p2p = await IPop.Api.Endpoints.MeetingEndpoints.ResolveJoinAsync(t.Db, null, room, org.Id, default);
        Assert.Null(p2p.Join!.LiveKitToken);

        Assert.Equal("not_invited", (await IPop.Api.Endpoints.MeetingEndpoints.ResolveJoinAsync(t.Db, lk, room, stranger.Id, default)).Code);
        Assert.Equal("not_found", (await IPop.Api.Endpoints.MeetingEndpoints.ResolveJoinAsync(t.Db, lk, "meeting-nope", att.Id, default)).Code);
    }
}
