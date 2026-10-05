using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Api.Endpoints.CalendarEndpoints;
using static IPop.Api.Endpoints.CalendarSharingEndpoints;

namespace IPop.Tests;

/// Calendar settings helpers: preview my calendar at each access level, and people asking to see more.
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
    public async Task Preview_matches_what_each_access_level_sees()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1"); var colleague = t.AddUser("C1");
        await Event(t, me.Id, "Standup", Day(2).AddHours(2));
        await Event(t, me.Id, "Doctor", Day(2).AddHours(6), "private");

        var everyone = await BuildPreviewAsync(t.Db, me.Id, null, Day(1), Day(4), default);
        Assert.Equal([null, null], everyone.Select(i => i.TheySee));
        Assert.Equal(["Standup", "Doctor"], everyone.Select(i => i.YourTitle));
        // The preview is exactly the real view of someone with no grant.
        var real = await BuildPeopleAsync(t.Db, colleague.Id, [me.Id], Day(1), Day(4), default);
        Assert.Equal(real.Select(r => r.Title), everyone.Select(i => i.TheySee));

        Assert.Equal(["Standup", null], (await BuildPreviewAsync(t.Db, me.Id, DelegateLevel.Titles, Day(1), Day(4), default)).Select(i => i.TheySee));
        Assert.Equal(["Standup", "Doctor"], (await BuildPreviewAsync(t.Db, me.Id, DelegateLevel.Full, Day(1), Day(4), default)).Select(i => i.TheySee));
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
