using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Api.Endpoints.CalendarEndpoints;
using static IPop.Api.Endpoints.CalendarSharingEndpoints;

namespace IPop.Tests;

/// Calendar sharing stage 1: other people see busy blocks, titles only for Public events,
/// invitees always see the meeting, and Find a time suggests slots free for everyone.
public class CalendarSharingTests
{
    private static DateTime Day(int offset) => DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(offset), DateTimeKind.Utc);
    private static readonly TimeZoneInfo Wib = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");

    private static async Task<CalendarEvent> Event(TestDb t, Guid owner, string title, DateTime start, int hours = 1, string? visibility = null,
        IReadOnlyList<Guid>? attendees = null)
    {
        var (r, ev, _) = await CreateEventAsync(t.Db, owner,
            new SaveEventRequest(title, start, start.AddHours(hours), Location: "Room 2", AttendeeIds: attendees, Visibility: visibility), default);
        Assert.Equal(SaveResult.Ok, r);
        return ev!;
    }

    [Fact]
    public async Task New_events_default_to_busy_and_follow_the_owner_default()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1");
        var a = await Event(t, me.Id, "A", Day(2).AddHours(3));
        Assert.Equal(CalendarVisibility.Busy, a.Visibility);

        await SaveSettingsAsync(t.Db, me.Id, new SettingsDto(CalendarVisibility.Public, true), default);
        var b = await Event(t, me.Id, "B", Day(2).AddHours(5));
        Assert.Equal(CalendarVisibility.Public, b.Visibility);

        // "Hide details" still creates Busy only events; an explicit choice wins.
        await SaveSettingsAsync(t.Db, me.Id, new SettingsDto(CalendarVisibility.Hidden, true), default);
        Assert.Equal(CalendarVisibility.Busy, (await Event(t, me.Id, "C", Day(3).AddHours(3))).Visibility);
        Assert.Equal(CalendarVisibility.Private, (await Event(t, me.Id, "D", Day(3).AddHours(5), visibility: "private")).Visibility);
    }

    [Fact]
    public async Task Others_see_titles_only_for_public_events()
    {
        using var t = new TestDb();
        var owner = t.AddUser("S1"); var viewer = t.AddUser("S2");
        await Event(t, owner.Id, "Open day", Day(2).AddHours(2), visibility: "public");
        await Event(t, owner.Id, "Board prep", Day(2).AddHours(4), visibility: "busy");
        await Event(t, owner.Id, "Doctor", Day(2).AddHours(6), visibility: "private");

        var items = await BuildPeopleAsync(t.Db, viewer.Id, [owner.Id], Day(1), Day(4), default);
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal("S1", i.Nik));
        Assert.Equal("Open day", items.Single(i => i.Visibility == "public").Title);
        Assert.Equal("Room 2", items.Single(i => i.Visibility == "public").Location);
        Assert.Null(items.Single(i => i.Visibility == "busy").Title);
        Assert.Null(items.Single(i => i.Visibility == "private").Title);
        Assert.Null(items.Single(i => i.Visibility == "private").Location);
    }

    [Fact]
    public async Task Hide_details_turns_public_into_busy_for_others()
    {
        using var t = new TestDb();
        var owner = t.AddUser("S1"); var viewer = t.AddUser("S2");
        await Event(t, owner.Id, "Open day", Day(2).AddHours(2), visibility: "public");
        await SaveSettingsAsync(t.Db, owner.Id, new SettingsDto(CalendarVisibility.Hidden, true), default);

        var item = Assert.Single(await BuildPeopleAsync(t.Db, viewer.Id, [owner.Id], Day(1), Day(4), default));
        Assert.Null(item.Title);
        Assert.Equal(CalendarVisibility.Busy, item.Visibility);
    }

    [Fact]
    public async Task Invitees_see_the_meeting_whatever_its_visibility()
    {
        using var t = new TestDb();
        var boss = t.AddUser("S1"); var guest = t.AddUser("S2"); var stranger = t.AddUser("S3");
        await Event(t, boss.Id, "Secret review", Day(2).AddHours(3), visibility: "private", attendees: [guest.Id]);

        var forGuest = Assert.Single(await BuildPeopleAsync(t.Db, guest.Id, [boss.Id], Day(1), Day(4), default));
        Assert.Equal("Secret review", forGuest.Title);
        Assert.NotNull(forGuest.MeetingId);

        var forStranger = Assert.Single(await BuildPeopleAsync(t.Db, stranger.Id, [boss.Id], Day(1), Day(4), default));
        Assert.Null(forStranger.Title);
        Assert.Null(forStranger.MeetingId);
    }

    [Fact]
    public async Task Busy_means_events_and_accepted_meetings_pending_is_tentative_declined_hidden()
    {
        using var t = new TestDb();
        var org = t.AddUser("S1"); var p = t.AddUser("S2"); var viewer = t.AddUser("S3");
        var pending = await Event(t, org.Id, "Pending", Day(2).AddHours(1), attendees: [p.Id]);
        var accepted = await Event(t, org.Id, "Accepted", Day(2).AddHours(3), attendees: [p.Id]);
        var declined = await Event(t, org.Id, "Declined", Day(2).AddHours(5), attendees: [p.Id]);
        await MeetingEndpoints.ApplyRsvpAsync(t.Db, accepted.MeetingId!.Value, p.Id, "accept", null, default);
        await MeetingEndpoints.ApplyRsvpAsync(t.Db, declined.MeetingId!.Value, p.Id, "decline", null, default);
        // A cancelled meeting never shows.
        var cancelled = await Event(t, org.Id, "Cancelled", Day(2).AddHours(7), attendees: [p.Id]);
        await MeetingEndpoints.ApplyCancelAsync(t.Db, cancelled.MeetingId!.Value, org.Id, default);

        var items = await BuildPeopleAsync(t.Db, viewer.Id, [p.Id], Day(1), Day(4), default);
        Assert.Equal(2, items.Count);
        Assert.True(items.Single(i => i.MeetingId is null && i.Id == $"meeting:{pending.MeetingId}").Tentative);
        Assert.False(items.Single(i => i.Id == $"meeting:{accepted.MeetingId}").Tentative);

        // The organizer's own meeting is never tentative for them.
        var orgItems = await BuildPeopleAsync(t.Db, viewer.Id, [org.Id], Day(1), Day(4), default);
        Assert.Equal(3, orgItems.Count);
        Assert.All(orgItems, i => Assert.False(i.Tentative));
    }

    [Fact]
    public async Task Tasks_and_inactive_people_are_not_shared()
    {
        using var t = new TestDb();
        var owner = t.AddUser("S1"); var gone = t.AddUser("S9", active: false); var viewer = t.AddUser("S2");
        var proj = t.AddProject("P", owner.Id);
        t.Db.ProjectTasks.Add(new ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = proj.Id, GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == proj.Id).Id,
            Title = "Task", Status = "todo", DueDate = Day(2), CreatedById = owner.Id,
        });
        t.Db.CalendarEvents.Add(new CalendarEvent { Id = Guid.NewGuid(), Title = "Ghost", OwnerId = gone.Id, StartAt = Day(2), EndAt = Day(2).AddHours(1), CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        Assert.Empty(await BuildPeopleAsync(t.Db, viewer.Id, [owner.Id, gone.Id], Day(1), Day(4), default));
    }

    [Fact]
    public async Task Other_calendars_list_assigns_colours_and_rejects_self_and_unknown()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1"); t.AddUser("S2"); t.AddUser("S3");
        Assert.Equal(AddOtherResult.Self, await AddOtherAsync(t.Db, me.Id, "S1", default));
        Assert.Equal(AddOtherResult.NotFound, await AddOtherAsync(t.Db, me.Id, "nobody", default));
        Assert.Equal(AddOtherResult.Added, await AddOtherAsync(t.Db, me.Id, "S2", default));
        Assert.Equal(AddOtherResult.Added, await AddOtherAsync(t.Db, me.Id, "S3", default));
        Assert.Equal(AddOtherResult.Added, await AddOtherAsync(t.Db, me.Id, "S2", default));   // idempotent

        var list = await ListOthersAsync(t.Db, me.Id, default);
        Assert.Equal(["S2", "S3"], list.Select(o => o.Nik));
        Assert.Equal(["a", "b"], list.Select(o => o.Color));
    }

    [Fact]
    public async Task Other_calendars_list_is_capped()
    {
        using var t = new TestDb();
        var me = t.AddUser("S0");
        for (var i = 1; i <= MaxOthers; i++) { t.AddUser($"X{i}"); Assert.Equal(AddOtherResult.Added, await AddOtherAsync(t.Db, me.Id, $"X{i}", default)); }
        t.AddUser("X99");
        Assert.Equal(AddOtherResult.TooMany, await AddOtherAsync(t.Db, me.Id, "X99", default));
    }

    [Fact]
    public async Task Detail_and_feed_carry_visibility_and_edit_changes_it_on_the_meeting()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1"); var guest = t.AddUser("S2");
        var ev = await Event(t, me.Id, "Sync", Day(2).AddHours(3), visibility: "public", attendees: [guest.Id]);
        Assert.Equal("public", (await LoadDetailAsync(t.Db, ev.Id, me.Id, default))!.Visibility);

        var (r, _, _) = await UpdateEventAsync(t.Db, ev.Id, me.Id,
            new SaveEventRequest("Sync", ev.StartAt, ev.EndAt, AttendeeIds: [guest.Id], Visibility: "private"), default);
        Assert.Equal(SaveResult.Ok, r);
        Assert.Equal("private", t.Db.Meetings.AsNoTracking().Single(m => m.Id == ev.MeetingId).Visibility);
        var feed = await BuildFeedAsync(t.Db, me.Id, Day(1), Day(4), FeedAccess.All, default);
        Assert.Equal("private", feed.Single(i => i.MeetingId == ev.MeetingId).Visibility);
    }

    [Fact]
    public void Suggestions_are_free_for_everyone_in_working_hours()
    {
        // Monday 2030-01-07 (local WIB), busy 08:00–10:00 and 11:00–12:00 local.
        var mondayLocal = new DateTime(2030, 1, 7);
        DateTime U(int h, int m = 0) => DateTime.SpecifyKind(mondayLocal.AddHours(h).AddMinutes(m).AddHours(-7), DateTimeKind.Utc);
        var busy = new List<(DateTime, DateTime)> { (U(8), U(10)), (U(11), U(12)) };

        var slots = SuggestSlots(busy, U(0), U(24), 60, Wib, U(0).AddDays(-1));
        Assert.Equal(U(10), slots[0]);
        Assert.DoesNotContain(slots, s => s < U(10) || (s >= U(10, 30) && s < U(12)));
        Assert.All(slots, s => Assert.True(s.AddHours(1) <= U(17)));
        Assert.Contains(U(12), slots);
    }

    [Fact]
    public void Suggestions_skip_weekends_and_the_past()
    {
        var saturdayLocal = new DateTime(2030, 1, 5);
        var from = DateTime.SpecifyKind(saturdayLocal.AddHours(-7), DateTimeKind.Utc);
        var slots = SuggestSlots([], from, from.AddDays(3), 30, Wib, from.AddDays(2).AddHours(12));   // "now" = Monday 12:00
        Assert.NotEmpty(slots);
        Assert.All(slots, s => Assert.Equal(DayOfWeek.Monday, s.AddHours(7).DayOfWeek));
        Assert.All(slots, s => Assert.True(s >= from.AddDays(2).AddHours(12)));
    }

    [Fact]
    public async Task Free_busy_lanes_put_me_first_and_ignore_the_edited_meeting()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1"); var other = t.AddUser("S2");
        var start = Day(5).AddHours(2);
        var m = await Event(t, me.Id, "Ours", start, attendees: [other.Id]);
        await Event(t, other.Id, "Theirs", start.AddHours(2));

        var fb = await BuildFreeBusyAsync(t.Db, me.Id, [other.Id, me.Id], Day(5), Day(6), 60, Wib, Day(0), default);
        Assert.Equal(["S1", "S2"], fb.Lanes.Select(l => l.Nik));
        Assert.Equal(2, fb.Lanes[1].Blocks.Count);
        Assert.True(fb.Lanes[1].Blocks.Single(b => b.StartAt == start).Tentative);

        var editing = await BuildFreeBusyAsync(t.Db, me.Id, [other.Id, me.Id], Day(5), Day(6), 60, Wib, Day(0), default, m.MeetingId);
        Assert.Empty(editing.Lanes[0].Blocks);
        Assert.Single(editing.Lanes[1].Blocks);
    }
}
