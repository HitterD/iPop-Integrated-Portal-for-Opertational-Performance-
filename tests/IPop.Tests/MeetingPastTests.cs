using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 §5 Past: GET /api/meetings/past — last 30 days, organiser or non-declined attendee, newest
/// first, pages of 20 by a "before" cursor, attendance count and only the recordings the viewer may watch.
public class MeetingPastTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 3, 0, 0, DateTimeKind.Utc);

    private static Meeting AddMeeting(TestDb t, Guid organizer, string title, DateTime start, string? location = "SJA Hall · Kalosi",
        string status = MeetingStatus.Active, params (Guid User, string Rsvp)[] attendees)
    {
        var m = new Meeting
        {
            Id = Guid.NewGuid(), Title = title, StartAt = start, EndAt = start.AddHours(1), OrganizerId = organizer, Location = location,
            Kind = MeetingKind.Infer(location, true), Status = status, CreatedAt = start.AddDays(-1),
        };
        m.Attendees.Add(new MeetingAttendee { Id = Guid.NewGuid(), UserId = organizer, RsvpStatus = "accept" });
        foreach (var (u, r) in attendees) m.Attendees.Add(new MeetingAttendee { Id = Guid.NewGuid(), UserId = u, RsvpStatus = r });
        t.Db.Meetings.Add(m);
        t.Db.SaveChanges();
        return m;
    }

    private static MeetingRecording AddRecording(TestDb t, Guid meetingId, Guid startedBy, MeetingRecordingStatus status, int? seconds, params Guid[] viewers)
    {
        var r = new MeetingRecording
        {
            Id = Guid.NewGuid(), RoomKey = "kalosi", Title = "Rec", EgressId = "EG_" + Guid.NewGuid().ToString("N")[..6], StartedById = startedBy,
            MeetingId = meetingId, StartedAt = Now.AddDays(-1), Status = status, DurationSeconds = seconds,
        };
        foreach (var v in viewers) r.Viewers.Add(new MeetingRecordingViewer { RecordingId = r.Id, UserId = v });
        t.Db.MeetingRecordings.Add(r);
        t.Db.SaveChanges();
        return r;
    }

    [Fact]
    public async Task Past_lists_my_ended_meetings_of_the_last_30_days_newest_first()
    {
        using var t = new TestDb();
        var me = t.AddUser("PAST_ME"); var other = t.AddUser("PAST_O");
        AddMeeting(t, me.Id, "Mine 2 days ago", Now.AddDays(-2));
        AddMeeting(t, other.Id, "Invited 1 day ago", Now.AddDays(-1), attendees: (me.Id, "accept"));
        AddMeeting(t, other.Id, "Declined", Now.AddDays(-1).AddHours(2), attendees: (me.Id, "decline"));
        AddMeeting(t, other.Id, "No answer", Now.AddDays(-3), attendees: (me.Id, "pending"));
        AddMeeting(t, me.Id, "31 days old", Now.AddDays(-31));
        AddMeeting(t, me.Id, "Cancelled", Now.AddDays(-4), status: MeetingStatus.Cancelled);
        AddMeeting(t, other.Id, "Not mine", Now.AddDays(-1));
        AddMeeting(t, me.Id, "Still running", Now.AddMinutes(-30));
        AddMeeting(t, me.Id, "Upcoming", Now.AddHours(2));

        await using var v = t.Verify();
        var page = await MeetingEndpoints.LoadPastAsync(v, me.Id, Now, 30, null, 20, null, default);

        Assert.Equal(["Invited 1 day ago", "Mine 2 days ago", "No answer"], page.Items.Select(p => p.Meeting.Title));
        Assert.Equal(3, page.Total);
        Assert.False(page.HasMore);
        Assert.Equal("room", page.Items[0].Meeting.Kind);
        Assert.Equal("kalosi", page.Items[0].Meeting.RoomKey);
        // Ended meetings never hand out the room password.
        Assert.All(page.Items, p => Assert.Null(p.Meeting.RoomPassword));
    }

    [Fact]
    public async Task Past_pages_by_the_before_cursor()
    {
        using var t = new TestDb();
        var me = t.AddUser("PAGE_ME");
        for (var i = 1; i <= 25; i++) AddMeeting(t, me.Id, $"M{i:00}", Now.AddHours(-i * 20));

        await using var v = t.Verify();
        var first = await MeetingEndpoints.LoadPastAsync(v, me.Id, Now, 30, null, 20, null, default);
        Assert.Equal(20, first.Items.Count);
        Assert.Equal(25, first.Total);
        Assert.True(first.HasMore);
        Assert.Equal("M01", first.Items[0].Meeting.Title);
        Assert.Equal("M20", first.Items[^1].Meeting.Title);

        var next = await MeetingEndpoints.LoadPastAsync(v, me.Id, Now, 30, first.Items[^1].Meeting.StartAt, 20, null, default);
        Assert.Equal(["M21", "M22", "M23", "M24", "M25"], next.Items.Select(p => p.Meeting.Title));
        Assert.False(next.HasMore);
    }

    [Fact]
    public async Task Past_recordings_are_only_those_the_viewer_may_watch()
    {
        using var t = new TestDb();
        var me = t.AddUser("REC_ME"); var other = t.AddUser("REC_O");
        var m = AddMeeting(t, other.Id, "Weekly ICT", Now.AddDays(-7), attendees: (me.Id, "accept"));
        var mine = AddRecording(t, m.Id, other.Id, MeetingRecordingStatus.Ready, 3120, me.Id, other.Id);
        AddRecording(t, m.Id, other.Id, MeetingRecordingStatus.Ready, 600, other.Id);            // I was not in the room
        AddRecording(t, m.Id, other.Id, MeetingRecordingStatus.Deleted, 600, me.Id);             // deleted
        AddRecording(t, m.Id, other.Id, MeetingRecordingStatus.Processing, null, me.Id);         // not ready yet
        var blocked = AddRecording(t, m.Id, other.Id, MeetingRecordingStatus.Ready, 900, me.Id); // the service says no (file gone)

        await using var v = t.Verify();
        var page = await MeetingEndpoints.LoadPastAsync(v, me.Id, Now, 30, null, 20, id => Task.FromResult(id != blocked.Id), default);

        var rec = Assert.Single(Assert.Single(page.Items).Recordings);
        Assert.Equal(mine.Id, rec.Id);
        Assert.Equal(3120, rec.DurationSeconds);
    }

    [Fact]
    public async Task Past_attendance_counts_people_seen_in_the_room_else_who_said_yes()
    {
        using var t = new TestDb();
        var me = t.AddUser("ATT_ME"); var a = t.AddUser("ATT_A"); var b = t.AddUser("ATT_B"); var guest = t.AddUser("ATT_G");
        var seen = AddMeeting(t, me.Id, "Recorded", Now.AddDays(-2), attendees: [(a.Id, "accept"), (b.Id, "pending")]);
        AddRecording(t, seen.Id, me.Id, MeetingRecordingStatus.Ready, 60, me.Id, b.Id);
        t.Db.MeetingRoomGuests.Add(new MeetingRoomGuest { MeetingId = seen.Id, UserId = guest.Id, AdmittedAt = Now.AddDays(-2) });
        t.Db.SaveChanges();
        AddMeeting(t, me.Id, "Not recorded", Now.AddDays(-3), attendees: [(a.Id, "accept"), (b.Id, "decline")]);

        await using var v = t.Verify();
        var page = await MeetingEndpoints.LoadPastAsync(v, me.Id, Now, 30, null, 20, null, default);

        // Seen in the room: recording viewers (me, b) + the password guest = 3.
        Assert.Equal(3, page.Items.Single(p => p.Meeting.Title == "Recorded").Attended);
        // Nothing observed: the organizer + accepted invitees = 2.
        Assert.Equal(2, page.Items.Single(p => p.Meeting.Title == "Not recorded").Attended);
    }

    [Fact]
    public async Task Past_days_is_clamped_and_never_reaches_further_than_90()
    {
        using var t = new TestDb();
        var me = t.AddUser("CL_ME");
        AddMeeting(t, me.Id, "60 days", Now.AddDays(-60));
        AddMeeting(t, me.Id, "100 days", Now.AddDays(-100));
        await using var v = t.Verify();
        Assert.Equal(["60 days"], (await MeetingEndpoints.LoadPastAsync(v, me.Id, Now, 500, null, 20, null, default)).Items.Select(p => p.Meeting.Title));
        Assert.Empty((await MeetingEndpoints.LoadPastAsync(v, me.Id, Now, 0, null, 20, null, default)).Items);
    }
}
