using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Api.Endpoints.CalendarEndpoints;
using static IPop.Api.Endpoints.CalendarSharingEndpoints;

namespace IPop.Tests;

/// The Share dialog's helpers: preview my calendar as a colleague or a delegate, apply the
/// default to events already on my calendar, and colleagues asking to see details.
public class CalendarShareUxTests
{
    private static DateTime Day(int offset) => DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(offset), DateTimeKind.Utc);

    private static async Task<CalendarEvent> Event(TestDb t, Guid owner, string title, DateTime start, string? visibility = null,
        IReadOnlyList<Guid>? attendees = null)
    {
        var (r, ev, _) = await CreateEventAsync(t.Db, owner,
            new SaveEventRequest(title, start, start.AddHours(1), AttendeeIds: attendees, Visibility: visibility), default);
        Assert.Equal(SaveResult.Ok, r);
        return ev!;
    }

    [Fact]
    public async Task Preview_matches_what_colleagues_and_delegates_see()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1"); var colleague = t.AddUser("C1");
        await Event(t, me.Id, "Standup", Day(2).AddHours(2), "public");
        await Event(t, me.Id, "Budget", Day(2).AddHours(4), "busy");
        await Event(t, me.Id, "Doctor", Day(2).AddHours(6), "private");

        var asColleague = await BuildPreviewAsync(t.Db, me.Id, asDelegate: false, hidden: null, Day(1), Day(4), default);
        Assert.Equal(["Standup", null, null], asColleague.Select(i => i.TheySee));
        Assert.Equal(["Standup", "Budget", "Doctor"], asColleague.Select(i => i.YourTitle));
        // The preview is exactly the real colleague view.
        var real = await BuildPeopleAsync(t.Db, colleague.Id, [me.Id], Day(1), Day(4), default);
        Assert.Equal(real.Select(r => r.Title), asColleague.Select(i => i.TheySee));

        var asDelegate = await BuildPreviewAsync(t.Db, me.Id, asDelegate: true, hidden: null, Day(1), Day(4), default);
        Assert.Equal(["Standup", "Budget", null], asDelegate.Select(i => i.TheySee));

        // The draft "Hide my calendar details" previews before it is saved.
        var hiddenDraft = await BuildPreviewAsync(t.Db, me.Id, asDelegate: false, hidden: true, Day(1), Day(4), default);
        Assert.All(hiddenDraft, i => Assert.Null(i.TheySee));
        Assert.Equal(CalendarVisibility.Busy, hiddenDraft[0].Visibility);
    }

    [Fact]
    public async Task Apply_to_upcoming_changes_future_events_and_my_meetings_but_not_private_or_past()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1"); var guest = t.AddUser("G1"); var other = t.AddUser("O1");
        var past = await Event(t, me.Id, "Past", Day(-3).AddHours(2), "busy");
        var future = await Event(t, me.Id, "Future", Day(3).AddHours(2), "busy");
        var secret = await Event(t, me.Id, "Doctor", Day(3).AddHours(5), "private");
        var mine = await Event(t, me.Id, "My meeting", Day(4).AddHours(2), "busy", [guest.Id]);
        var theirs = await Event(t, other.Id, "Their meeting", Day(4).AddHours(5), "busy", [me.Id]);

        var changed = await ApplyToUpcomingAsync(t.Db, me.Id, CalendarVisibility.Public, DateTime.UtcNow, default);
        Assert.Equal(2, changed);
        string Vis(Guid id) => t.Db.CalendarEvents.AsNoTracking().Single(e => e.Id == id).Visibility;
        Assert.Equal(CalendarVisibility.Busy, Vis(past.Id));
        Assert.Equal(CalendarVisibility.Public, Vis(future.Id));
        Assert.Equal(CalendarVisibility.Private, Vis(secret.Id));
        Assert.Equal(CalendarVisibility.Public, (await t.Db.Meetings.AsNoTracking().SingleAsync(m => m.Id == mine.MeetingId)).Visibility);
        // A meeting someone else organizes keeps their choice.
        Assert.Equal(CalendarVisibility.Busy, (await t.Db.Meetings.AsNoTracking().SingleAsync(m => m.Id == theirs.MeetingId)).Visibility);

        // "Hide my calendar details" maps back to Busy only.
        await ApplyToUpcomingAsync(t.Db, me.Id, CalendarVisibility.Hidden, DateTime.UtcNow, default);
        Assert.Equal(CalendarVisibility.Busy, Vis(future.Id));
    }

    [Fact]
    public async Task Asking_to_see_details_notifies_once_a_day()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1"); t.AddUser("C1");
        var now = DateTime.UtcNow;
        Assert.Equal(AskResult.Sent, await AskForDetailsAsync(t.Db, null, null, me.Id, "C1", now, default));
        Assert.Equal(AskResult.AlreadyAsked, await AskForDetailsAsync(t.Db, null, null, me.Id, "C1", now.AddHours(2), default));
        Assert.Equal(AskResult.Sent, await AskForDetailsAsync(t.Db, null, null, me.Id, "C1", now.AddDays(1).AddMinutes(1), default));
        Assert.Equal(AskResult.Self, await AskForDetailsAsync(t.Db, null, null, me.Id, "S1", now, default));
        Assert.Equal(AskResult.NotFound, await AskForDetailsAsync(t.Db, null, null, me.Id, "NOPE", now, default));
    }
}
