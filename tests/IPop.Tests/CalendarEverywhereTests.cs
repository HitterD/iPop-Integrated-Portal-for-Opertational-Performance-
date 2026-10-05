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
    public void Expansion_only_includes_matching_weekdays_and_valid_local_times()
    {
        var weekly = CalendarRecurrence.Parse("W:4;C:2")!.Expand(Utc(2026, 10, 12, 2), Wib);
        Assert.Equal([Utc(2026, 10, 15, 2), Utc(2026, 10, 22, 2)], weekly);
        Assert.Empty(CalendarRecurrence.Parse("W:4;U:2026-10-14")!.Expand(Utc(2026, 10, 12, 2), Wib));
        Assert.Empty(CalendarRecurrence.Parse("D;U:2026-10-11")!.Expand(Utc(2026, 10, 12, 2), Wib));
        foreach (var invalid in new[] { "D;C:0", "D;C:101", "W:0,1;C:2", "D;U:bad", "D;C:2;U:2026-10-20", "D;X:1" })
            Assert.Null(CalendarRecurrence.Parse(invalid));
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var spring = CalendarRecurrence.Parse("D;C:3")!.Expand(Utc(2026, 3, 7, 7, 30), eastern);
        Assert.Equal([Utc(2026, 3, 7, 7, 30), Utc(2026, 3, 9, 6, 30), Utc(2026, 3, 10, 6, 30)], spring);
        var fall = CalendarRecurrence.Parse("D;C:3")!.Expand(Utc(2026, 10, 31, 5, 30), eastern);
        Assert.Equal([Utc(2026, 10, 31, 5, 30), Utc(2026, 11, 1, 6, 30), Utc(2026, 11, 2, 6, 30)], fall);
    }

    [Fact]
    public async Task Invalid_series_rules_do_not_create_a_single_event()
    {
        using var t = new TestDb(); var owner = t.AddUser("RULE");
        var at = Utc(2026, 10, 12, 9);
        foreach (var rule in new[] { "D;C:0", "X", "D;U:2026-10-11" })
        {
            var (result, ev, _) = await CreateSeriesAsync(t.Db, owner.Id, new SaveEventRequest("Invalid", at, at.AddHours(1), Recurrence: rule), default);
            Assert.Equal(SaveResult.InvalidRange, result); Assert.Null(ev);
        }
        Assert.Empty(await t.Db.CalendarEvents.ToListAsync());
    }

    [Fact]
    public async Task Series_create_rolls_back_every_row_when_later_insert_fails()
    {
        using var t = new TestDb(); var owner = t.AddUser("TX1"); var guest = t.AddUser("TX2");
        await t.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_second_event BEFORE INSERT ON CalendarEvents WHEN (SELECT COUNT(*) FROM CalendarEvents) >= 1 BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
        var at = NextMonday().AddHours(9);
        await Assert.ThrowsAsync<DbUpdateException>(() => CreateSeriesAsync(t.Db, owner.Id,
            new SaveEventRequest("Atomic", at, at.AddHours(1), AttendeeIds: [guest.Id], ReminderMinutes: 10, Recurrence: "D;C:3"), default));
        using var verify = t.Verify();
        Assert.Empty(await verify.CalendarEvents.ToListAsync());
        Assert.Empty(await verify.Meetings.ToListAsync());
        Assert.Empty(await verify.MeetingAttendees.ToListAsync());
        Assert.Empty(await verify.MeetingReminders.ToListAsync());
    }

    [Fact]
    public async Task Series_edit_and_delete_roll_back_when_a_later_row_fails()
    {
        using var t = new TestDb(); var owner = t.AddUser("TX3");
        var at = NextMonday().AddHours(9);
        var (_, first, _) = await CreateSeriesAsync(t.Db, owner.Id, new SaveEventRequest("Original", at, at.AddHours(1), Recurrence: "D;C:3"), default);
        var rows = await t.Db.CalendarEvents.AsNoTracking().OrderBy(e => e.StartAt).ToListAsync();
        var last = rows[^1].Id.ToString().ToUpperInvariant();
        await t.Db.Database.ExecuteSqlRawAsync($"CREATE TRIGGER reject_last_update BEFORE UPDATE ON CalendarEvents WHEN OLD.Id = '{last}' BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => UpdateSeriesAsync(t.Db, first!.Id, owner.Id, new SaveEventRequest("Changed", at.AddHours(1), at.AddHours(2)), "all", default));
        using (var verify = t.Verify()) Assert.All(await verify.CalendarEvents.ToListAsync(), e => Assert.Equal("Original", e.Title));
        await t.Db.Database.ExecuteSqlRawAsync($"CREATE TRIGGER reject_last_delete BEFORE DELETE ON CalendarEvents WHEN OLD.Id = '{last}' BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => DeleteSeriesAsync(t.Db, first!.Id, owner.Id, "all", default));
        using (var verify = t.Verify()) Assert.Equal(3, await verify.CalendarEvents.CountAsync());
    }

    [Fact]
    public async Task Scoped_delegate_authorization_checks_private_siblings_before_any_write()
    {
        using var t = new TestDb(); var owner = t.AddUser("PR1"); var actor = t.AddUser("PR2");
        await CalendarDelegateEndpoints.GrantAsync(t.Db, null, null, owner.Id, actor.Nik, DelegateLevel.Edit, owner.Id, default);
        var at = NextMonday().AddHours(9);
        var (_, first, _) = await CreateSeriesAsync(t.Db, owner.Id, new SaveEventRequest("Original", at, at.AddHours(1), Recurrence: "D;C:3"), default);
        var sibling = await t.Db.CalendarEvents.OrderBy(e => e.StartAt).LastAsync();
        sibling.Visibility = CalendarVisibility.Private; await t.Db.SaveChangesAsync();
        Assert.Equal(SaveResult.Forbidden, (await UpdateSeriesAsync(t.Db, first!.Id, owner.Id, new SaveEventRequest("Changed", at, at.AddHours(1)), "all", default, actor.Id)).Item1);
        Assert.Equal(SaveResult.Forbidden, (await DeleteSeriesAsync(t.Db, first!.Id, owner.Id, "all", default, actor.Id)).Item1);
        Assert.All(await t.Db.CalendarEvents.AsNoTracking().ToListAsync(), e => Assert.Equal("Original", e.Title));
        await CalendarDelegateEndpoints.GrantAsync(t.Db, null, null, owner.Id, actor.Nik, DelegateLevel.Full, owner.Id, default);
        Assert.Equal(owner.Id, (await CalendarDelegateEndpoints.ActorForEventAsync(t.Db, sibling.Id, actor.Id, default)).OwnerId);
        Assert.Equal(SaveResult.Ok, (await UpdateSeriesAsync(t.Db, first!.Id, owner.Id, new SaveEventRequest("Changed", at, at.AddHours(1)), "all", default, actor.Id)).Item1);
        Assert.Equal(SaveResult.Ok, (await DeleteSeriesAsync(t.Db, first!.Id, owner.Id, "all", default, actor.Id)).Item1);
    }

    [Fact]
    public async Task Scoped_edits_keep_wall_time_across_dst_and_refresh_removed_attendees()
    {
        using var t = new TestDb(); var owner = t.AddUser("DST"); var oldGuest = t.AddUser("OLD"); var newGuest = t.AddUser("NEW");
        owner.TimeZoneId = "America/New_York"; await t.Db.SaveChangesAsync();
        var at = Utc(2027, 3, 13, 14);
        var (_, first, _) = await CreateSeriesAsync(t.Db, owner.Id, new SaveEventRequest("DST", at, at.AddHours(1), AttendeeIds: [oldGuest.Id], Recurrence: "D;C:3"), default);
        var invites = new Dictionary<Guid, List<Guid>>();
        var (result, _, touched) = await UpdateSeriesAsync(t.Db, first!.Id, owner.Id, new SaveEventRequest("DST", at.AddHours(1), at.AddHours(2), AttendeeIds: [newGuest.Id]), "all", default, invitations: invites);
        Assert.Equal(SaveResult.Ok, result);
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.All(await t.Db.CalendarEvents.AsNoTracking().ToListAsync(), e => Assert.Equal(10, TimeZoneInfo.ConvertTimeFromUtc(e.StartAt, eastern).Hour));
        Assert.Contains(oldGuest.Id, touched); Assert.Contains(newGuest.Id, touched);
        Assert.Equal(3, invites.Count); Assert.All(invites.Values, users => Assert.Equal([newGuest.Id], users));
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
