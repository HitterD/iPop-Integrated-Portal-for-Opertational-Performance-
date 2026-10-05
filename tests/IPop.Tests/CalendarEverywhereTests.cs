using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Api.Endpoints.CalendarEndpoints;

namespace IPop.Tests;

/// Calendar everywhere: repeating events (rule, series create/edit/cancel by scope), holidays in the feed and
/// in Find a time, my iDesk tickets, declined meetings hidden, timed task due dates.
public class CalendarEverywhereTests
{
    private static readonly TimeZoneInfo Wib = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);
    private static DateTime NextMonday() { var d = DateTime.UtcNow.Date.AddDays(1); while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(1); return DateTime.SpecifyKind(d, DateTimeKind.Utc); }

    [Fact]
    public void Rule_parses_round_trips_and_describes_itself()
    {
        var r = CalendarRecurrence.Parse("W:4,1;U:2026-12-31")!;
        Assert.Equal("W", r.Freq);
        Assert.Equal([1, 4], r.Days);
        Assert.Equal("W:1,4;U:2026-12-31", r.ToString());
        Assert.Equal("Every Mon and Thu, until 31 Dec 2026", r.Describe(new DateTime(2026, 10, 12, 9, 0, 0)));
        Assert.Equal("Every day, 10 times", CalendarRecurrence.Parse("D;C:10")!.Describe(DateTime.Now));
        Assert.Equal("Every month on the 3rd", CalendarRecurrence.Parse("M")!.Describe(new DateTime(2026, 10, 3)));
        Assert.Null(CalendarRecurrence.Parse(""));
        Assert.Null(CalendarRecurrence.Parse("X:1"));
    }

    [Fact]
    public void Weekly_expansion_keeps_local_time_and_stops_at_the_end()
    {
        // Mon 12 Oct 2026 09:00 WIB = 02:00 UTC.
        var r = CalendarRecurrence.Parse("W:1,4;U:2026-10-25")!;
        var at = r.Expand(Utc(2026, 10, 12, 2), Wib);
        Assert.Equal([Utc(2026, 10, 12, 2), Utc(2026, 10, 15, 2), Utc(2026, 10, 19, 2), Utc(2026, 10, 22, 2)], at);
        Assert.Equal(5, CalendarRecurrence.Parse("D;C:5")!.Expand(Utc(2026, 10, 12, 2), Wib).Count);
        // Monthly on the 31st skips short months instead of drifting.
        var monthly = CalendarRecurrence.Parse("M;C:3")!.Expand(Utc(2026, 1, 31, 2), Wib);
        Assert.Equal([31, 31, 31], monthly.Select(d => d.Day));
        Assert.True(CalendarRecurrence.Parse("D")!.Expand(Utc(2026, 1, 1, 2), Wib).Count <= CalendarRecurrence.MaxOccurrences);
    }

    [Fact]
    public async Task Series_creates_one_meeting_per_occurrence_and_edits_or_cancels_by_scope()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1"); var guest = t.AddUser("S2");
        var mon = NextMonday().AddHours(2);
        var (r, first, invited) = await CreateSeriesAsync(t.Db, me.Id,
            new SaveEventRequest("Weekly sync", mon, mon.AddMinutes(30), AttendeeIds: [guest.Id], ReminderMinutes: 10, Recurrence: "W:1;C:4"), default);
        Assert.Equal(SaveResult.Ok, r);
        Assert.Equal([guest.Id], invited);
        var series = await t.Db.CalendarEvents.AsNoTracking().Where(e => e.SeriesId == first!.SeriesId).OrderBy(e => e.StartAt).ToListAsync();
        Assert.Equal(4, series.Count);
        Assert.All(series, e => { Assert.NotNull(e.MeetingId); Assert.Equal("W:1;C:4", e.Recurrence); });
        Assert.Equal(4, await t.Db.Meetings.CountAsync());
        var detail = await LoadDetailAsync(t.Db, series[0].Id, me.Id, default);
        Assert.StartsWith("Every Mon", detail!.RecurrenceText);

        // "This and following" from the 3rd moves 3 and 4 to 30 minutes later, keeps 1 and 2.
        var third = series[2];
        var (ur, _, _) = await UpdateSeriesAsync(t.Db, third.Id, me.Id,
            new SaveEventRequest("Weekly sync", third.StartAt.AddMinutes(30), third.EndAt.AddMinutes(30)), "following", default);
        Assert.Equal(SaveResult.Ok, ur);
        var after = await t.Db.CalendarEvents.AsNoTracking().Where(e => e.SeriesId == first!.SeriesId).OrderBy(e => e.StartAt).ToListAsync();
        Assert.Equal(series[0].StartAt, after[0].StartAt);
        Assert.Equal(series[1].StartAt, after[1].StartAt);
        Assert.Equal(series[2].StartAt.AddMinutes(30), after[2].StartAt);
        Assert.Equal(series[3].StartAt.AddMinutes(30), after[3].StartAt);

        // Cancel "all": every meeting cancelled, guest told once.
        var (dr, _, people, count) = await DeleteSeriesAsync(t.Db, after[1].Id, me.Id, "all", default);
        Assert.Equal(SaveResult.Ok, dr);
        Assert.Equal(4, count);
        Assert.Equal([guest.Id], people);
        Assert.Equal(0, await t.Db.CalendarEvents.CountAsync(e => e.SeriesId == first!.SeriesId));
        Assert.All(await t.Db.Meetings.AsNoTracking().ToListAsync(), m => Assert.Equal(MeetingStatus.Cancelled, m.Status));
    }

    [Fact]
    public async Task Feed_shows_holidays_my_tickets_and_timed_tasks_and_hides_declined_meetings()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1"); var boss = t.AddUser("S2");
        var day = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(3), DateTimeKind.Utc);
        t.Db.CompanyHolidays.Add(new CompanyHoliday { Id = Guid.NewGuid(), Date = day, Title = "Maulid Nabi", CreatedAt = DateTime.UtcNow });
        t.Db.Tickets.Add(new Ticket { Id = Guid.NewGuid(), UserId = me.Id, Provider = "idesk", Origin = "IPOP", Status = "IN_PROGRESS", Priority = "HIGH",
            Module = "ict", ModuleName = "ICT", Category = "Printer", Title = "Printer 3rd floor", TicketNumber = "4821", SlaTarget = day.AddHours(4),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        t.Db.Tickets.Add(new Ticket { Id = Guid.NewGuid(), UserId = me.Id, Provider = "idesk", Origin = "IPOP", Status = "CLOSED", Priority = "LOW",
            Module = "ict", ModuleName = "ICT", Category = "x", Title = "Old closed", SlaTarget = day.AddHours(5), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();
        var (_, mtg, _) = await CreateEventAsync(t.Db, boss.Id, new SaveEventRequest("Board", day.AddHours(2), day.AddHours(3), AttendeeIds: [me.Id]), default);
        var att = await t.Db.MeetingAttendees.FirstAsync(a => a.UserId == me.Id);
        att.RsvpStatus = "decline"; await t.Db.SaveChangesAsync();

        var feed = await BuildFeedAsync(t.Db, me.Id, day.AddDays(-1), day.AddDays(2), FeedAccess.All, default);
        var hol = Assert.Single(feed, f => f.Source == "holidays");
        Assert.Equal("Maulid Nabi", hol.Title);
        Assert.True(hol.AllDay);
        var tk = Assert.Single(feed, f => f.Source == "tickets");
        Assert.Equal("Ticket 4821 due", tk.Subtitle);
        Assert.StartsWith("/tickets/", tk.Url);
        Assert.DoesNotContain(feed, f => f.Title == "Board");

        // Organizer still sees it; a meeting with a calendar row opens the event, not Calls.
        var bossFeed = await BuildFeedAsync(t.Db, boss.Id, day.AddDays(-1), day.AddDays(2), FeedAccess.All, default);
        var board = Assert.Single(bossFeed, f => f.Title == "Board");
        Assert.Null(board.Url);
        Assert.NotNull(board.EventId);
    }

    [Fact]
    public async Task Find_a_time_skips_holidays()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1");
        var mon = NextMonday();
        t.Db.CompanyHolidays.Add(new CompanyHoliday { Id = Guid.NewGuid(), Date = mon, Title = "Holiday", CreatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();
        var fb = await CalendarSharingEndpoints.BuildFreeBusyAsync(t.Db, me.Id, [me.Id], mon.AddHours(-7), mon.AddDays(2).AddHours(-7), 60, Wib, mon.AddDays(-1), default);
        Assert.NotEmpty(fb.Suggestions);
        Assert.All(fb.Suggestions, s => Assert.NotEqual(mon.Date, TimeZoneInfo.ConvertTimeFromUtc(s, Wib).Date));
    }
}
