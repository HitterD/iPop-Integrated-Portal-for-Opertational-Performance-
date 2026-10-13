using System.Security.Claims;
using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Application.MeetingRooms;
using IPop.Core.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Api.Endpoints.CalendarEndpoints;

namespace IPop.Tests;

/// Meeting scheduling v3 §3: repeat for room bookings. Preview each occurrence (free / booked by, other rooms free), then
/// save only the kept ones: Skip drops a date, Move books that date in another room. Every occurrence is its own meeting
/// with its own password; a clash that appears between preview and save fails the whole series with the date.
public class RoomSeriesTests
{
    private static readonly TimeZoneInfo Wib = RoomBookingRules.ResolveZone(null);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// The next Monday at least 2 days ahead, 10:00 WIB, in UTC.
    private static DateTime FirstMonday(int hour = 10)
    {
        var d = DateOnly.FromDateTime(RoomBookingRules.Local(DateTime.UtcNow, Wib)).AddDays(2);
        while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(1);
        return RoomBookingRules.ToUtc(d.ToDateTime(new TimeOnly(hour, 0)), Wib);
    }

    private static DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(RoomBookingRules.Local(utc, Wib));

    private static async Task<Guid> BookAsync(TestDb t, Guid organizer, DateTime start, string room, string title, params string[] niks)
    {
        var (r, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Verify(), organizer,
            new(title, start, start.AddHours(1), null, room, null, null, null, null, null, niks), default);
        Assert.Equal(MeetingEndpoints.CreateResult.Created, r);
        return id!.Value;
    }

    private static SaveEventRequest Weekly(DateTime start, int times = 6, IReadOnlyList<OccurrenceOverride>? overrides = null, string room = "SJA Hall · Kalosi") =>
        new("Weekly ICT", start, start.AddHours(1), Location: room, Recurrence: $"W:1;C:{times}", Overrides: overrides);

    private static HttpContext Http(User u) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("nik", u.Nik)], "Bearer")),
    };
    private static int Status(IResult r) => r is IStatusCodeHttpResult s ? s.StatusCode ?? 200 : 200;
    private static JsonElement Body(IResult r) => r is IValueHttpResult { Value: { } v } ? JsonSerializer.SerializeToElement(v, Web) : default;

    // ---------------------------------------------------------------- preview

    [Fact]
    public async Task Preview_lists_every_date_with_the_clash_titled_by_the_privacy_rule_and_the_free_rooms()
    {
        using var t = new TestDb(); var me = t.AddUser("ME"); var other = t.AddUser("OT");
        var first = FirstMonday();
        var third = first.AddDays(14);
        await BookAsync(t, other.Id, third, "SJA Hall · Kalosi", "Budget review");
        await BookAsync(t, other.Id, third, "SJA Hall · Robusta", "Robusta thing");

        var (error, preview) = await PreviewRoomSeriesAsync(t.Db, me.Id, new SeriesPreviewRequest("kalosi", first, first.AddHours(1), "W:1;C:6"), default);
        Assert.Null(error);
        Assert.Equal((6, 1), (preview!.Total, preview.Booked));
        Assert.Equal(Enumerable.Range(0, 6).Select(i => LocalDate(first.AddDays(7 * i))), preview.Occurrences.Select(o => o.Date));
        var clash = preview.Occurrences[2];
        Assert.Equal((false, "Booked", "booked"), (clash.Free, clash.BookedBy, clash.Reason));          // not my meeting: no title
        Assert.Equal(["arabika", "kapal-api", "toraja"], clash.FreeRooms);                          // Robusta busy, Kalosi is the room itself
        Assert.All(preview.Occurrences.Where((_, i) => i != 2), o => Assert.Equal((true, (string?)null, (string?)null), (o.Free, o.BookedBy, o.Reason)));
        Assert.Equal((first.AddDays(14), first.AddDays(14).AddHours(1)), (clash.StartAt, clash.EndAt));
        Assert.DoesNotContain("Budget", JsonSerializer.Serialize(preview));

        // The organizer of the clashing booking sees its title.
        var (_, theirs) = await PreviewRoomSeriesAsync(t.Db, other.Id, new SeriesPreviewRequest("kalosi", first, first.AddHours(1), "W:1;C:6"), default);
        Assert.Equal("Budget review", theirs!.Occurrences[2].BookedBy);
    }

    [Fact]
    public async Task Preview_applies_the_limits_and_checks_room_and_rule()
    {
        using var t = new TestDb(); var me = t.AddUser("ME");
        var first = FirstMonday();
        Assert.Equal("unknown_room", (await PreviewRoomSeriesAsync(t.Db, me.Id, new("gayo", first, first.AddHours(1), "W:1;C:3"), default)).Error);
        Assert.Equal("invalid_recurrence", (await PreviewRoomSeriesAsync(t.Db, me.Id, new("kalosi", first, first.AddHours(1), "X"), default)).Error);
        Assert.Equal("too_long", (await PreviewRoomSeriesAsync(t.Db, me.Id, new("kalosi", first, first.AddHours(9), "W:1;C:3"), default)).Error);
        var past = DateTime.UtcNow.AddDays(-7);
        Assert.Equal("in_past", (await PreviewRoomSeriesAsync(t.Db, me.Id, new("kalosi", past, past.AddHours(1), "W:1;C:3"), default)).Error);
        var far = first.AddDays(70);
        Assert.Equal("too_far", (await PreviewRoomSeriesAsync(t.Db, me.Id, new("kalosi", far, far.AddHours(1), "W:1;C:3"), default)).Error);

        // Only the first date has to be within 60 days (mockup: "60-day limit applies to the first date").
        var (err, twelve) = await PreviewRoomSeriesAsync(t.Db, me.Id, new("kalosi", first, first.AddHours(1), "W:1;C:12"), default);
        Assert.Null(err);
        Assert.Equal((12, 0), (twelve!.Total, twelve.Booked));

        var http = await PreviewAsync(new("kalosi", first, first.AddHours(9), "W:1;C:3"), Http(me), t.Db, default);
        Assert.Equal((400, "invalid_range", "too_long"),
            (Status(http), Body(http).GetProperty("code").GetString(), Body(http).GetProperty("reason").GetString()));
        Assert.Equal(404, Status(await PreviewAsync(new("gayo", first, first.AddHours(1), "W:1;C:3"), Http(me), t.Db, default)));
        Assert.Equal(200, Status(await PreviewAsync(new("kalosi", first, first.AddHours(1), "W:1;C:3"), Http(me), t.Db, default)));
    }

    [Fact]
    public async Task Preview_except_series_ignores_the_series_being_edited()
    {
        using var t = new TestDb(); var me = t.AddUser("ME");
        var first = FirstMonday();
        var (r, ev, _) = await CreateSeriesAsync(t.Db, me.Id, Weekly(first, 3), default);
        Assert.Equal(SaveResult.Ok, r);
        var (_, again) = await PreviewRoomSeriesAsync(t.Db, me.Id, new("kalosi", first, first.AddHours(1), "W:1;C:3"), default);
        Assert.Equal(3, again!.Booked);
        Assert.All(again.Occurrences, o => Assert.Equal("Weekly ICT", o.BookedBy));                // mine: titled
        var (_, editing) = await PreviewRoomSeriesAsync(t.Db, me.Id, new("kalosi", first, first.AddHours(1), "W:1;C:3", ev!.SeriesId), default);
        Assert.Equal(0, editing!.Booked);
    }

    // ---------------------------------------------------------------- create with overrides

    [Fact]
    public async Task Skip_on_the_clashing_date_books_the_other_five_with_their_own_passwords_in_one_series()
    {
        using var t = new TestDb(); var me = t.AddUser("ME"); var other = t.AddUser("OT");
        var first = FirstMonday();
        await BookAsync(t, other.Id, first.AddDays(14), "SJA Hall · Kalosi", "Budget review");

        // Without a decision the clash fails the whole series with its date, nothing saved.
        var issue = new SaveIssue();
        var (fail, _, _) = await CreateSeriesAsync(t.Db, me.Id, Weekly(first), default, issue: issue);
        Assert.Equal(SaveResult.RoomBooked, fail);
        Assert.Equal(LocalDate(first.AddDays(14)), issue.Date);
        Assert.Equal(1, await t.Verify().Meetings.CountAsync());

        // Review focus 3: Skip date 3 of 6 → exactly 5 meetings, 5 distinct passwords, one SeriesId, Kind room.
        var (r, ev, _) = await CreateSeriesAsync(t.Verify(), me.Id, Weekly(first, overrides: [new(LocalDate(first.AddDays(14)), true, null)]), default);
        Assert.Equal(SaveResult.Ok, r);
        using var db = t.Verify();
        var rows = await db.CalendarEvents.Where(e => e.SeriesId == ev!.SeriesId).OrderBy(e => e.StartAt).ToListAsync();
        Assert.Equal(5, rows.Count);
        Assert.DoesNotContain(rows, e => e.StartAt == first.AddDays(14));
        var ids = rows.Select(e => e.MeetingId!.Value).ToList();
        var meetings = await db.Meetings.Where(m => ids.Contains(m.Id)).ToListAsync();
        Assert.Equal(5, meetings.Count);
        Assert.Equal(5, meetings.Select(m => m.RoomPassword).Distinct().Count());
        Assert.All(meetings, m => Assert.Equal((MeetingKind.Room, "SJA Hall · Kalosi"), (m.Kind, m.Location)));
        Assert.All(meetings, m => Assert.Matches("^[0-9]{6}$", m.RoomPassword));
    }

    [Fact]
    public async Task Move_books_that_date_in_the_other_room()
    {
        using var t = new TestDb(); var me = t.AddUser("ME"); var other = t.AddUser("OT");
        var first = FirstMonday();
        var clashDay = LocalDate(first.AddDays(7));
        await BookAsync(t, other.Id, first.AddDays(7), "SJA Hall · Kalosi", "Budget review");

        var (r, ev, _) = await CreateSeriesAsync(t.Db, me.Id, Weekly(first, 3, [new(clashDay, false, "toraja")]), default);
        Assert.Equal(SaveResult.Ok, r);
        var rows = await t.Verify().CalendarEvents.Where(e => e.SeriesId == ev!.SeriesId).OrderBy(e => e.StartAt).ToListAsync();
        Assert.Equal(["SJA Hall · Kalosi", "SJA Hall · Toraja", "SJA Hall · Kalosi"], rows.Select(e => e.Location));
        var moved = await t.Verify().Meetings.SingleAsync(m => m.Id == rows[1].MeetingId);
        Assert.Equal((MeetingKind.Room, "SJA Hall · Toraja", "Weekly ICT"), (moved.Kind, moved.Location, moved.Title));
        Assert.NotNull(moved.RoomPassword);
    }

    [Fact]
    public async Task Move_to_a_busy_room_or_an_unknown_room_fails_with_the_date()
    {
        using var t = new TestDb(); var me = t.AddUser("ME"); var other = t.AddUser("OT");
        var first = FirstMonday();
        var day2 = LocalDate(first.AddDays(7));
        await BookAsync(t, other.Id, first.AddDays(7), "SJA Hall · Kalosi", "Budget review");
        await BookAsync(t, other.Id, first.AddDays(7), "SJA Hall · Toraja", "Board");

        var issue = new SaveIssue();
        var (r, _, _) = await CreateSeriesAsync(t.Db, me.Id, Weekly(first, 3, [new(day2, false, "toraja")]), default, issue: issue);
        Assert.Equal((SaveResult.RoomBooked, day2), (r, issue.Date));
        Assert.Equal(2, await t.Verify().Meetings.CountAsync());

        var (u, _, _) = await CreateSeriesAsync(t.Verify(), me.Id, Weekly(first, 3, [new(day2, false, "gayo")]), default);
        Assert.Equal(SaveResult.RoomRequired, u);
    }

    [Fact]
    public async Task Skipping_every_date_saves_nothing()
    {
        using var t = new TestDb(); var me = t.AddUser("ME");
        var first = FirstMonday();
        var all = Enumerable.Range(0, 3).Select(i => new OccurrenceOverride(LocalDate(first.AddDays(7 * i)), true, null)).ToList();
        var (r, _, _) = await CreateSeriesAsync(t.Db, me.Id, Weekly(first, 3, all), default);
        Assert.Equal(SaveResult.NothingToSave, r);
        Assert.Empty(t.Verify().Meetings);
        var http = ErrorFor(r, null);
        Assert.Equal((400, "nothing_to_save"), (Status(http), Body(http).GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_booking_made_between_preview_and_save_fails_the_series_with_its_date_and_saves_nothing()
    {
        using var t = new TestDb(); var me = t.AddUser("ME"); var other = t.AddUser("OT");
        var first = FirstMonday();
        await BookAsync(t, other.Id, first.AddDays(14), "SJA Hall · Kalosi", "Budget review");
        var (_, preview) = await PreviewRoomSeriesAsync(t.Db, me.Id, new("kalosi", first, first.AddHours(1), "W:1;C:6"), default);
        Assert.Equal(1, preview!.Booked);

        // Someone books date 4 after the preview (race).
        await BookAsync(t, other.Id, first.AddDays(21), "SJA Hall · Kalosi", "Late booking");
        var before = await t.Verify().Meetings.CountAsync();
        var issue = new SaveIssue();
        var (r, _, _) = await CreateSeriesAsync(t.Verify(), me.Id, Weekly(first, overrides: [new(LocalDate(first.AddDays(14)), true, null)]), default, issue: issue);
        Assert.Equal(SaveResult.RoomBooked, r);
        Assert.Equal(LocalDate(first.AddDays(21)), issue.Date);
        Assert.Equal(before, await t.Verify().Meetings.CountAsync());
        Assert.Equal(2, await t.Verify().CalendarEvents.CountAsync());

        var http = ErrorFor(r, issue);
        Assert.Equal(409, Status(http));
        Assert.Equal(("room_booked", LocalDate(first.AddDays(21)).ToString("yyyy-MM-dd")),
            (Body(http).GetProperty("code").GetString(), Body(http).GetProperty("date").GetString()));
    }

    [Fact]
    public async Task Room_series_after_60_days_is_fine_when_the_first_date_is_within_60()
    {
        using var t = new TestDb(); var me = t.AddUser("ME");
        var first = FirstMonday();
        var (r, ev, _) = await CreateSeriesAsync(t.Db, me.Id, Weekly(first, 12), default);
        Assert.Equal(SaveResult.Ok, r);
        Assert.Equal(12, await t.Verify().CalendarEvents.CountAsync(e => e.SeriesId == ev!.SeriesId));
    }

    [Fact]
    public async Task Editing_a_whole_room_series_that_runs_past_60_days_keeps_working()
    {
        using var t = new TestDb(); var me = t.AddUser("ME");
        var first = FirstMonday();
        var (_, ev, _) = await CreateSeriesAsync(t.Db, me.Id, Weekly(first, 12), default);
        // Move every date 30 minutes later: dates 9–12 are beyond 60 days but the series' first date is not.
        var (r, _, _) = await UpdateSeriesAsync(t.Verify(), ev!.Id, me.Id,
            new SaveEventRequest("Weekly ICT", first.AddMinutes(30), first.AddMinutes(90), Location: "SJA Hall · Kalosi"), "all", default);
        Assert.Equal(SaveResult.Ok, r);
        Assert.All(await t.Verify().Meetings.ToListAsync(), m => Assert.Equal(30, RoomBookingRules.Local(m.StartAt, Wib).Minute));
        // A title-only edit of one far date keeps its start: allowed.
        var last = await t.Verify().CalendarEvents.OrderByDescending(e => e.StartAt).FirstAsync();
        var (one, _, _) = await UpdateEventAsync(t.Verify(), last.Id, me.Id,
            new SaveEventRequest("Renamed", last.StartAt, last.EndAt, Location: "SJA Hall · Kalosi"), default);
        Assert.Equal(SaveResult.Ok, one);
        // Moving that far date to another room is a new booking there: refused (more than 60 days ahead).
        var issue = new SaveIssue();
        var (moved, _, _) = await UpdateEventAsync(t.Verify(), last.Id, me.Id,
            new SaveEventRequest("Renamed", last.StartAt, last.EndAt, Location: "SJA Hall · Toraja"), default, issue: issue);
        Assert.Equal((SaveResult.InvalidRoomRange, "too_far"), (moved, issue.Reason));
    }

    [Fact]
    public async Task Non_room_series_ignore_room_limits_and_still_honour_skips()
    {
        using var t = new TestDb(); var me = t.AddUser("ME");
        var first = FirstMonday();
        var (r, ev, _) = await CreateSeriesAsync(t.Db, me.Id,
            new SaveEventRequest("Long workshop", first, first.AddHours(9), Location: "ICT office", Recurrence: "W:1;C:3",
                Overrides: [new(LocalDate(first.AddDays(7)), true, null)]), default);
        Assert.Equal(SaveResult.Ok, r);
        Assert.Equal(2, await t.Verify().CalendarEvents.CountAsync(e => e.SeriesId == ev!.SeriesId));
    }
}
