using System.Security.Claims;
using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Api.MeetingRooms;
using IPop.Application.MeetingRooms;
using IPop.Core.Entities;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Tests.MeetingRoomServiceTests;

namespace IPop.Tests;

/// Meeting scheduling v3 §2: the booking sheet's day timeline, "Other rooms free at this time" and the server-side booking
/// limits (15 min – 8 h, same local day, not in the past, up to 60 days ahead) that replace the 30/60/90 Use-now cap.
public class MeetingRoomAvailabilityTests
{
    private static readonly TimeZoneInfo Wib = RoomBookingRules.ResolveZone(null);

    private static MeetingRoomService Make(TestDb t) =>
        new(t.Db, new FakeRoomAdmin(), new FakeTokens(), new DbSettings(t.Db), new RoomPasswordLimiter(),
            new RoomMeetingCreator(t.Db), new RoomOccupancyTracker(DateTime.MinValue, TimeSpan.MaxValue));

    /// A local (WIB) time on a day a few days ahead, in UTC.
    private static DateTime At(DateOnly day, int hour, int minute = 0) =>
        RoomBookingRules.ToUtc(day.ToDateTime(new TimeOnly(hour, minute)), Wib);

    private static DateOnly Day(int ahead = 3) => DateOnly.FromDateTime(RoomBookingRules.Local(DateTime.UtcNow, Wib)).AddDays(ahead);

    private static async Task<Guid> BookAsync(TestDb t, Guid organizer, DateTime start, DateTime end, string room = "SJA Hall · Kalosi",
        string title = "Weekly ICT", params string[] niks)
    {
        var (r, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Verify(), organizer,
            new(title, start, end, null, room, null, null, null, null, null, niks), default);
        Assert.Equal(MeetingEndpoints.CreateResult.Created, r);
        return id!.Value;
    }

    private static HttpContext Http(User u) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("nik", u.Nik)], "Bearer")),
    };
    private static int Status(IResult r) => r is IStatusCodeHttpResult s ? s.StatusCode ?? 200 : 200;
    private static JsonElement Body(IResult r) => r is IValueHttpResult { Value: { } v } ? JsonSerializer.SerializeToElement(v, Web) : default;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- rules

    [Fact]
    public void Validate_enforces_15_minutes_to_8_hours_same_day_not_past_and_60_days()
    {
        var now = DateTime.UtcNow;
        var d = Day(3);
        Assert.Equal("too_short", RoomBookingRules.Validate(At(d, 10), At(d, 10, 14), now, Wib));
        Assert.Null(RoomBookingRules.Validate(At(d, 10), At(d, 10, 15), now, Wib));
        Assert.Null(RoomBookingRules.Validate(At(d, 9), At(d, 17), now, Wib));                        // exactly 8 h
        Assert.Equal("too_long", RoomBookingRules.Validate(At(d, 9), At(d, 17, 1), now, Wib));
        Assert.Equal("different_day", RoomBookingRules.Validate(At(d, 23), At(d.AddDays(1), 0, 30), now, Wib));
        Assert.Null(RoomBookingRules.Validate(At(d, 23), At(d.AddDays(1), 0), now, Wib));              // ends at midnight
        Assert.Equal("in_past", RoomBookingRules.Validate(now.AddMinutes(-2), now.AddMinutes(30), now, Wib));
        // This minute (Use now floors to the minute) is not the past.
        var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (RoomBookingRules.Local(now, Wib).Hour < 23) Assert.Null(RoomBookingRules.Validate(minute, minute.AddMinutes(30), now, Wib));
        Assert.Equal("too_far", RoomBookingRules.Validate(At(Day(61), 10), At(Day(61), 11), now, Wib));
        Assert.Null(RoomBookingRules.Validate(At(Day(60), 10), At(Day(60), 11), now, Wib));
        Assert.Equal((15, 480, 60), (RoomBookingRules.Limits.MinMinutes, RoomBookingRules.Limits.MaxMinutes, RoomBookingRules.Limits.MaxDaysAhead));
    }

    [Fact]
    public void NextFree_finds_the_next_slot_of_the_same_length_or_null()
    {
        var d = Day(3);
        var busy = new List<(DateTime, DateTime)> { (At(d, 10), At(d, 11)), (At(d, 11, 30), At(d, 12)) };
        // 1 h wanted at 10:00: 11:00–12:00 clashes with 11:30, so 12:00–13:00.
        Assert.Equal((At(d, 12), At(d, 13)), RoomBookingRules.NextFree(busy, At(d, 10), TimeSpan.FromHours(1), At(d, 19)));
        // 30 min fits right after the first booking.
        Assert.Equal((At(d, 11), At(d, 11, 30)), RoomBookingRules.NextFree(busy, At(d, 10, 15), TimeSpan.FromMinutes(30), At(d, 19)));
        Assert.Null(RoomBookingRules.NextFree(busy, At(d, 10), TimeSpan.FromHours(1), At(d, 12, 30)));
    }

    // ---------------------------------------------------------------- day availability

    [Fact]
    public async Task Day_lists_blocks_in_order_with_titles_only_for_people_in_the_meeting()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var guest = t.AddUser("G1"); var outsider = t.AddUser("X1");
        var d = Day(3);
        var late = await BookAsync(t, org.Id, At(d, 14), At(d, 15), title: "Budget review", niks: "G1");
        var early = await BookAsync(t, org.Id, At(d, 9), At(d, 10), title: "Weekly ICT");
        await BookAsync(t, org.Id, At(d, 9), At(d, 10), room: "SJA Hall · Toraja", title: "Other room");
        await BookAsync(t, org.Id, At(d.AddDays(1), 9), At(d.AddDays(1), 10), title: "Next day");
        var cancelled = await BookAsync(t, org.Id, At(d, 16), At(d, 17), title: "Dropped");
        await MeetingEndpoints.ApplyCancelAsync(t.Verify(), cancelled, org.Id, default);

        var mine = (await Make(t).DayAsync(org.Id, "kalosi", d, default))!;
        Assert.Equal(("kalosi", "Kalosi", 20, d), (mine.Key, mine.Name, mine.Capacity, mine.Date));
        Assert.Equal([early, late], mine.Blocks.Select(b => b.MeetingId!.Value));
        Assert.All(mine.Blocks, b => Assert.True(b.Mine && b.Visible));
        Assert.Equal((At(d, 7), At(d, 19)), (mine.WindowStart, mine.WindowEnd));

        var invited = (await Make(t).DayAsync(guest.Id, "kalosi", d, default))!;
        Assert.Equal(["Booked", "Budget review"], invited.Blocks.Select(b => b.Title));
        Assert.Equal((false, false), (invited.Blocks[0].Visible, invited.Blocks[0].Mine));
        Assert.Null(invited.Blocks[0].MeetingId);                                              // no id leaks either
        Assert.Equal((true, false), (invited.Blocks[1].Visible, invited.Blocks[1].Mine));

        // Review focus 4: someone outside the meeting sees "Booked" and the times, never the title.
        var other = (await Make(t).DayAsync(outsider.Id, "kalosi", d, default))!;
        Assert.All(other.Blocks, b => Assert.Equal(("Booked", false, (Guid?)null), (b.Title, b.Visible, b.MeetingId)));
        Assert.Equal([(At(d, 9), At(d, 10)), (At(d, 14), At(d, 15))], other.Blocks.Select(b => (b.StartAt, b.EndAt)));
        Assert.DoesNotContain("Budget", JsonSerializer.Serialize(other));

        // A declined attendee no longer sees the title.
        using (var db = t.Verify())
        {
            db.MeetingAttendees.Single(a => a.MeetingId == late && a.UserId == guest.Id).RsvpStatus = "decline";
            db.SaveChanges();
        }
        Assert.All((await Make(t).DayAsync(guest.Id, "kalosi", d, default))!.Blocks, b => Assert.Equal("Booked", b.Title));
    }

    [Fact]
    public async Task Day_window_widens_to_whole_hours_around_bookings_outside_7_to_19()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var d = Day(3);
        await BookAsync(t, org.Id, At(d, 18, 30), At(d, 20));
        await BookAsync(t, org.Id, At(d, 6, 15), At(d, 6, 45));
        var day = (await Make(t).DayAsync(org.Id, "kalosi", d, default))!;
        Assert.Equal((At(d, 6), At(d, 20)), (day.WindowStart, day.WindowEnd));
    }

    [Fact]
    public async Task Day_of_an_unknown_room_is_null_and_the_endpoint_checks_room_and_date()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var svc = Make(t);
        Assert.Null(await svc.DayAsync(me.Id, "gayo", Day(1), default));

        Assert.Equal(200, Status(await MeetingRoomEndpoints.AvailabilityAsync("kalosi", Day(1).ToString("yyyy-MM-dd"), Http(me), t.Db, svc, default)));
        Assert.Equal(404, Status(await MeetingRoomEndpoints.AvailabilityAsync("gayo", Day(1).ToString("yyyy-MM-dd"), Http(me), t.Db, svc, default)));
        Assert.Equal(400, Status(await MeetingRoomEndpoints.AvailabilityAsync("kalosi", "13/10/2026", Http(me), t.Db, svc, default)));
        Assert.Equal(400, Status(await MeetingRoomEndpoints.AvailabilityAsync("kalosi", null, Http(me), t.Db, svc, default)));
        var far = await MeetingRoomEndpoints.AvailabilityAsync("kalosi", Day(61).ToString("yyyy-MM-dd"), Http(me), t.Db, svc, default);
        Assert.Equal((400, "too_far"), (Status(far), Body(far).GetProperty("code").GetString()));
        Assert.Equal(200, Status(await MeetingRoomEndpoints.AvailabilityAsync("kalosi", Day(60).ToString("yyyy-MM-dd"), Http(me), t.Db, svc, default)));
    }

    [Fact]
    public void Limits_endpoint_returns_the_server_limits()
    {
        var body = Body(MeetingRoomEndpoints.LimitsResponse());
        Assert.Equal((15, 480, 60), (body.GetProperty("minMinutes").GetInt32(), body.GetProperty("maxMinutes").GetInt32(),
            body.GetProperty("maxDaysAhead").GetInt32()));
    }

    // ---------------------------------------------------------------- other rooms free

    [Fact]
    public async Task Free_at_marks_busy_rooms_with_the_title_rule_and_except_frees_the_edited_meeting()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var outsider = t.AddUser("X1");
        var d = Day(3);
        var kalosi = await BookAsync(t, org.Id, At(d, 10), At(d, 11), title: "Weekly ICT");
        await BookAsync(t, org.Id, At(d, 10, 30), At(d, 12), room: "SJA Hall · Toraja", title: "Board");
        await BookAsync(t, org.Id, At(d, 11), At(d, 12), room: "SJA Hall · Robusta", title: "Back to back");   // touches only

        var mine = await Make(t).FreeAtAsync(org.Id, At(d, 10), At(d, 11), null, default);
        Assert.Equal(["robusta", "arabika", "kalosi", "kapal-api", "toraja"], mine.Select(r => r.Key));
        Assert.Equal([true, true, false, true, false], mine.Select(r => r.Free));
        Assert.Equal("Weekly ICT", mine.Single(r => r.Key == "kalosi").BusyTitle);
        Assert.Null(mine.Single(r => r.Key == "robusta").BusyTitle);

        var other = await Make(t).FreeAtAsync(outsider.Id, At(d, 10), At(d, 11), null, default);
        Assert.Equal("Booked", other.Single(r => r.Key == "kalosi").BusyTitle);
        Assert.Equal("Booked", other.Single(r => r.Key == "toraja").BusyTitle);

        var editing = await Make(t).FreeAtAsync(org.Id, At(d, 10), At(d, 11), kalosi, default);
        Assert.True(editing.Single(r => r.Key == "kalosi").Free);

        Assert.Equal(200, Status(await MeetingRoomEndpoints.FreeAsync(At(d, 10), At(d, 11), null, Http(org), t.Db, Make(t), default)));
        Assert.Equal(400, Status(await MeetingRoomEndpoints.FreeAsync(At(d, 11), At(d, 10), null, Http(org), t.Db, Make(t), default)));
        Assert.Equal(400, Status(await MeetingRoomEndpoints.FreeAsync(null, At(d, 10), null, Http(org), t.Db, Make(t), default)));
    }

    // ---------------------------------------------------------------- use now with the v3 limits

    [Fact]
    public async Task Use_now_takes_an_end_time_up_to_8_hours_without_the_old_90_minute_cap()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var now = DateTime.UtcNow;
        var local = RoomBookingRules.Local(now, Wib);
        if (local.Hour >= 21) return;   // 2 h would cross local midnight: covered by the different_day case below
        var r = await Make(t).UseNowAsync(me.Id, "kalosi", now.AddHours(2), null, [], default);
        Assert.Null(r.Error);
        var m = t.Verify().Meetings.Single(x => x.Id == r.MeetingId);
        Assert.InRange((m.EndAt - m.StartAt).TotalMinutes, 119, 121);
        Assert.Equal(MeetingKind.Room, m.Kind);
    }

    [Fact]
    public async Task Use_now_minutes_overload_follows_the_same_limits()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var svc = Make(t);
        Assert.Equal("too_short", (await svc.UseNowAsync(me.Id, "robusta", 14, null, [], default)).Error);
        Assert.Equal("too_long", (await svc.UseNowAsync(me.Id, "robusta", 481, null, [], default)).Error);
        if (RoomBookingRules.Local(DateTime.UtcNow, Wib).Hour < 23)
            Assert.Null((await svc.UseNowAsync(me.Id, "robusta", 45, null, [], default)).Error);   // any length now, not only 30/60/90
        var crossing = (int)Math.Ceiling((RoomBookingRules.ToUtc(RoomBookingRules.Local(DateTime.UtcNow, Wib).Date.AddDays(1), Wib) - DateTime.UtcNow).TotalMinutes) + 20;
        if (crossing <= 480) Assert.Equal("different_day", (await svc.UseNowAsync(me.Id, "toraja", crossing, null, [], default)).Error);
    }

    [Fact]
    public async Task Use_now_with_the_next_booking_in_40_minutes_offers_at_most_30_and_refuses_45()
    {
        using var t = new TestDb(); var me = t.AddUser("U1"); var org = t.AddUser("O1");
        // Review focus 2: next booking in 40 min → its lock window opens in 30, so 45 is refused with maxMinutes ≤ 30.
        var next = DateTime.UtcNow.AddMinutes(40);
        using (var db = t.Verify())
        {
            db.Meetings.Add(new Meeting { Id = Guid.NewGuid(), Title = "Board", StartAt = next, EndAt = next.AddHours(1), OrganizerId = org.Id,
                Status = MeetingStatus.Active, Location = "SJA Hall · Kalosi", Kind = MeetingKind.Room, CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
        var svc = Make(t);
        var r = await svc.UseNowAsync(me.Id, "kalosi", DateTime.UtcNow.AddMinutes(45), null, [], default);
        Assert.Equal("room_booked", r.Error);
        Assert.InRange(r.MaxMinutes!.Value, 15, 30);

        var http = MeetingRoomEndpoints.UseNowError(r.Error, r.MaxMinutes);
        Assert.Equal(409, Status(http));
        Assert.Equal("room_booked", Body(http).GetProperty("code").GetString());
        Assert.InRange(Body(http).GetProperty("maxMinutes").GetInt32(), 15, 30);
    }

    [Fact]
    public void Use_now_limit_errors_map_to_invalid_range_with_the_reason()
    {
        var http = MeetingRoomEndpoints.UseNowError("too_long", null);
        Assert.Equal(400, Status(http));
        var body = Body(http);
        Assert.Equal(("invalid_range", "too_long", "A room booking is at most 8 hours."),
            (body.GetProperty("code").GetString(), body.GetProperty("reason").GetString(), body.GetProperty("message").GetString()));
        Assert.Equal(404, Status(MeetingRoomEndpoints.UseNowError("unknown_room", null)));
    }

    // ---------------------------------------------------------------- meeting + calendar writes of room meetings

    [Fact]
    public async Task Meeting_create_and_update_of_a_room_apply_the_limits()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var d = Day(3);
        var (r, _, _) = await MeetingEndpoints.ApplyCreateAsync(t.Verify(), org.Id,
            new("Long", At(d, 8), At(d, 17), null, "SJA Hall · Kalosi", null, null, null, null, null), default);
        Assert.Equal(MeetingEndpoints.CreateResult.InvalidRoomRange, r);
        // In person / voice are not limited.
        var (ok, _, _) = await MeetingEndpoints.ApplyCreateAsync(t.Verify(), org.Id,
            new("Workshop", At(d, 8), At(d, 17), null, "ICT office", null, null, null, null, null), default);
        Assert.Equal(MeetingEndpoints.CreateResult.Created, ok);

        var id = await BookAsync(t, org.Id, At(d, 9), At(d, 10));
        var (u, _, _) = await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), id, org.Id,
            new MeetingEndpoints.UpdateMeetingRequest("Weekly ICT", At(d, 9), At(d, 9, 10), "SJA Hall · Kalosi", null, null, null, null), default);
        Assert.Equal(MeetingEndpoints.UpdateResult.InvalidRoomRange, u);
        var (far, _, _) = await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), id, org.Id,
            new MeetingEndpoints.UpdateMeetingRequest("Weekly ICT", At(Day(70), 9), At(Day(70), 10), "SJA Hall · Kalosi", null, null, null, null), default);
        Assert.Equal(MeetingEndpoints.UpdateResult.InvalidRoomRange, far);
        var (fine, _, _) = await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), id, org.Id,
            new MeetingEndpoints.UpdateMeetingRequest("Weekly ICT", At(d, 9), At(d, 12), "SJA Hall · Kalosi", null, null, null, null), default);
        Assert.Equal(MeetingEndpoints.UpdateResult.Applied, fine);
    }

    [Fact]
    public async Task Editing_a_room_meeting_that_already_started_keeps_working_without_moving_its_start()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var start = DateTime.UtcNow.AddMinutes(-20);
        start = new DateTime(start.Ticks - start.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var id = Guid.NewGuid();
        using (var db = t.Verify())
        {
            db.Meetings.Add(new Meeting { Id = id, Title = "Running", StartAt = start, EndAt = start.AddMinutes(60), OrganizerId = org.Id,
                Status = MeetingStatus.Active, Location = "SJA Hall · Kalosi", Kind = MeetingKind.Room, CreatedAt = DateTime.UtcNow });
            db.MeetingAttendees.Add(new MeetingAttendee { Id = Guid.NewGuid(), MeetingId = id, UserId = org.Id, RsvpStatus = "accept", Required = true });
            db.SaveChanges();
        }
        // "Extend 15 min" goes through the same update path: a started booking may move its end.
        Assert.Null(await new RoomMeetingCreator(t.Verify()).SetEndAsync(org.Id, id, start.AddMinutes(75), default));
        // Moving the start into the past is refused.
        var (r, _, _) = await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), id, org.Id,
            new MeetingEndpoints.UpdateMeetingRequest("Running", start.AddMinutes(-30), start.AddMinutes(75), "SJA Hall · Kalosi", null, null, null, null), default);
        Assert.Equal(MeetingEndpoints.UpdateResult.InvalidRoomRange, r);
    }

    [Fact]
    public async Task Calendar_create_of_a_9_hour_room_meeting_is_refused_with_the_reason()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var d = Day(3);
        var issue = new CalendarEndpoints.SaveIssue();
        var (r, _, _) = await CalendarEndpoints.CreateEventAsync(t.Db, org.Id,
            new CalendarEndpoints.SaveEventRequest("Offsite", At(d, 8), At(d, 17), Location: "SJA Hall · Kalosi"), default, issue: issue);
        Assert.Equal(CalendarEndpoints.SaveResult.InvalidRoomRange, r);
        Assert.Equal("too_long", issue.Reason);
        Assert.Empty(t.Verify().Meetings);

        var http = CalendarEndpoints.ErrorFor(r, issue);
        Assert.Equal(400, Status(http));
        Assert.Equal(("invalid_range", "too_long"), (Body(http).GetProperty("code").GetString(), Body(http).GetProperty("reason").GetString()));

        // A plain personal event of 9 h is fine.
        var (p, _, _) = await CalendarEndpoints.CreateEventAsync(t.Db, org.Id,
            new CalendarEndpoints.SaveEventRequest("Offsite", At(d, 8), At(d, 17), Location: "Bandung"), default);
        Assert.Equal(CalendarEndpoints.SaveResult.Ok, p);
        // Voice on a hall place is not a room booking.
        var (v, _, _) = await CalendarEndpoints.CreateEventAsync(t.Db, org.Id,
            new CalendarEndpoints.SaveEventRequest("Call", At(d, 8), At(d, 17), Location: "SJA Hall · Kalosi", Kind: "voice"), default);
        Assert.Equal(CalendarEndpoints.SaveResult.Ok, v);
    }

    [Fact]
    public async Task Calendar_edit_of_a_room_meeting_into_the_past_is_refused()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var d = Day(3);
        var (_, ev, _) = await CalendarEndpoints.CreateEventAsync(t.Db, org.Id,
            new CalendarEndpoints.SaveEventRequest("Room", At(d, 9), At(d, 10), Location: "SJA Hall · Kalosi"), default);
        var past = DateTime.UtcNow.AddDays(-1);
        var (r, _, _) = await CalendarEndpoints.UpdateEventAsync(t.Verify(), ev!.Id, org.Id,
            new CalendarEndpoints.SaveEventRequest("Room", past, past.AddHours(1), Location: "SJA Hall · Kalosi"), default);
        Assert.Equal(CalendarEndpoints.SaveResult.InvalidRoomRange, r);
    }
}
