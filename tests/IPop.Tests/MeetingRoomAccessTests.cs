using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Api.MeetingRooms;
using IPop.Application.Calls;
using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
using IPop.Core.Entities;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Tests.MeetingRoomServiceTests;

namespace IPop.Tests;

/// Meeting rooms v2 §1: a room is only usable inside a booking. Organizer and non-declined attendees walk in; anyone
/// else needs the booking's 6-digit password (then stays admitted as a guest); "Use now" books the room on the spot.
public class MeetingRoomAccessTests
{
    private static MeetingRoomService Make(TestDb t, FakeRoomAdmin? lk = null, RoomPasswordLimiter? limiter = null) =>
        new(t.Db, lk ?? new FakeRoomAdmin(), new FakeTokens(), new DbSettings(t.Db), limiter ?? new RoomPasswordLimiter(),
            new RoomMeetingCreator(t.Db));

    private static async Task<Guid> BookAsync(TestDb t, Guid organizer, DateTime start, int minutes, string room = "SJA Hall · Kalosi",
        params Guid[] attendees)
    {
        var (r, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Verify(), organizer,
            new("Weekly sync", start, start.AddMinutes(minutes), null, room, null, attendees, null, null, null), default);
        Assert.Equal(MeetingEndpoints.CreateResult.Created, r);
        return id!.Value;
    }

    private static string PasswordOf(TestDb t, Guid meetingId) => t.Verify().Meetings.Single(m => m.Id == meetingId).RoomPassword!;

    private static string Wrong(string pw) => ((int.Parse(pw) + 1) % 1_000_000).ToString("D6");

    private static void Decline(TestDb t, Guid meetingId, Guid userId)
    {
        using var db = t.Verify();
        db.MeetingAttendees.Single(a => a.MeetingId == meetingId && a.UserId == userId).RsvpStatus = "decline";
        db.SaveChanges();
    }

    // ---------------------------------------------------------------- passwords on meetings

    [Fact]
    public async Task Hall_room_meetings_get_a_random_6_digit_password_and_other_places_do_not()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var start = DateTime.UtcNow.AddHours(2);
        var hall = await BookAsync(t, org.Id, start, 30);
        var other = await BookAsync(t, org.Id, start, 30, "Lantai 3");

        Assert.Matches("^[0-9]{6}$", PasswordOf(t, hall));
        Assert.Null(t.Verify().Meetings.Single(m => m.Id == other).RoomPassword);

        // Moving a meeting into a hall room gives it a password.
        var upd = new MeetingEndpoints.UpdateMeetingRequest("Weekly sync", start, start.AddMinutes(30), "SJA Hall · Toraja", null, null, null, null);
        Assert.Equal(MeetingEndpoints.UpdateResult.Applied, (await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), other, org.Id, upd, default)).Result);
        Assert.Matches("^[0-9]{6}$", PasswordOf(t, other));
    }

    [Fact]
    public async Task Calendar_bookings_of_a_hall_room_also_get_a_password()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var start = DateTime.UtcNow.AddHours(2);
        var (r, ev, _) = await CalendarEndpoints.CreateEventAsync(t.Db, org.Id,
            new CalendarEndpoints.SaveEventRequest("Room", start, start.AddHours(1), Location: "SJA Hall · Robusta"), default);
        Assert.Equal(CalendarEndpoints.SaveResult.Ok, r);
        Assert.Matches("^[0-9]{6}$", PasswordOf(t, ev!.MeetingId!.Value));
    }

    [Fact]
    public async Task Regenerating_changes_the_password_keeps_admitted_guests_and_is_organizer_only()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var guest = t.AddUser("G1"); var late = t.AddUser("L1");
        var id = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60);
        var old = PasswordOf(t, id);
        var svc = Make(t);
        Assert.Null((await svc.JoinAsync(guest.Id, "kalosi", old, default)).Error);

        Assert.Equal(MeetingEndpoints.RoomPasswordResult.Forbidden, (await MeetingEndpoints.ApplyNewRoomPasswordAsync(t.Verify(), id, guest.Id, default)).Result);
        var (res, pw) = await MeetingEndpoints.ApplyNewRoomPasswordAsync(t.Verify(), id, org.Id, default);
        Assert.Equal(MeetingEndpoints.RoomPasswordResult.Applied, res);
        Assert.Matches("^[0-9]{6}$", pw);
        Assert.Equal(pw, PasswordOf(t, id));
        Assert.NotEqual(old, pw);

        Assert.Null((await svc.JoinAsync(guest.Id, "kalosi", null, default)).Error);                 // Review Focus 3: guest stays
        Assert.Equal("wrong_password", (await svc.JoinAsync(late.Id, "kalosi", old, default)).Error); // old one stopped working
        Assert.Null((await svc.JoinAsync(late.Id, "kalosi", pw, default)).Error);
    }

    [Fact]
    public async Task Regenerating_a_non_hall_or_cancelled_meeting_is_refused()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var other = await BookAsync(t, org.Id, DateTime.UtcNow.AddHours(1), 30, "Lantai 3");
        Assert.Equal(MeetingEndpoints.RoomPasswordResult.NotHallRoom, (await MeetingEndpoints.ApplyNewRoomPasswordAsync(t.Verify(), other, org.Id, default)).Result);
        Assert.Equal(MeetingEndpoints.RoomPasswordResult.NotFound, (await MeetingEndpoints.ApplyNewRoomPasswordAsync(t.Verify(), Guid.NewGuid(), org.Id, default)).Result);
        var hall = await BookAsync(t, org.Id, DateTime.UtcNow.AddHours(1), 30);
        await MeetingEndpoints.ApplyCancelAsync(t.Verify(), hall, org.Id, default);
        Assert.Equal(MeetingEndpoints.RoomPasswordResult.Cancelled, (await MeetingEndpoints.ApplyNewRoomPasswordAsync(t.Verify(), hall, org.Id, default)).Result);
    }

    // ---------------------------------------------------------------- join

    [Fact]
    public async Task Organizer_and_invited_people_join_without_a_password()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var inv = t.AddUser("I1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60, attendees: inv.Id);
        var svc = Make(t);
        Assert.Null((await svc.JoinAsync(org.Id, "kalosi", null, default)).Error);
        Assert.Null((await svc.JoinAsync(inv.Id, "kalosi", null, default)).Error);
    }

    [Fact]
    public async Task Outsider_needs_the_password_and_admin_has_no_bypass()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var admin = t.AddAdmin("ADM");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60);
        var r = await Make(t).JoinAsync(admin.Id, "kalosi", null, default);
        Assert.Equal("password_required", r.Error);
        Assert.Null(r.Join);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_user_out_of_that_booking_even_with_the_right_one()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var x = t.AddUser("X1"); var y = t.AddUser("Y1");
        var id = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60);
        var pw = PasswordOf(t, id);
        var svc = Make(t);
        for (var i = 0; i < 5; i++) Assert.Equal("wrong_password", (await svc.JoinAsync(x.Id, "kalosi", Wrong(pw), default)).Error);
        Assert.Equal("too_many_attempts", (await svc.JoinAsync(x.Id, "kalosi", pw, default)).Error);
        Assert.Empty(t.Verify().MeetingRoomGuests);
        // Per user: someone else is not affected.
        Assert.Null((await svc.JoinAsync(y.Id, "kalosi", pw, default)).Error);
    }

    [Fact]
    public async Task Correct_password_admits_a_guest_who_rejoins_without_it()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var g = t.AddUser("G1");
        var id = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60);
        var pw = PasswordOf(t, id);
        var svc = Make(t);
        // Typed the way the invite shows it ("123 456").
        Assert.Null((await svc.JoinAsync(g.Id, "kalosi", $"{pw[..3]} {pw[3..]}", default)).Error);
        var row = t.Verify().MeetingRoomGuests.Single();
        Assert.Equal((id, g.Id), (row.MeetingId, row.UserId));
        Assert.Null((await svc.JoinAsync(g.Id, "kalosi", null, default)).Error);
        Assert.Single(t.Verify().MeetingRoomGuests);   // not admitted twice
        Assert.Equal("guest", (await RoomAccess.ForAsync(t.Verify(), "kalosi", g.Id, DateTime.UtcNow, false, default)).Role);
    }

    [Fact]
    public async Task Declined_attendee_is_an_outsider()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var no = t.AddUser("N1");
        var id = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60, attendees: no.Id);
        Decline(t, id, no.Id);
        Assert.Equal("password_required", (await Make(t).JoinAsync(no.Id, "kalosi", null, default)).Error);
        Assert.Equal("outsider", (await RoomAccess.ForAsync(t.Verify(), "kalosi", no.Id, DateTime.UtcNow, false, default)).Role);
    }

    [Fact]
    public async Task Attendee_removed_from_the_booking_after_joining_needs_the_password_to_rejoin()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var a = t.AddUser("A1");
        var start = DateTime.UtcNow.AddMinutes(-5);
        var id = await BookAsync(t, org.Id, start, 60, attendees: a.Id);
        var svc = Make(t);
        Assert.Null((await svc.JoinAsync(a.Id, "kalosi", null, default)).Error);

        var upd = new MeetingEndpoints.UpdateMeetingRequest("Weekly sync", start, start.AddMinutes(60), "SJA Hall · Kalosi", null, null, [], null);
        Assert.Equal(MeetingEndpoints.UpdateResult.Applied, (await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), id, org.Id, upd, default)).Result);
        Assert.Equal("password_required", (await svc.JoinAsync(a.Id, "kalosi", null, default)).Error);
    }

    [Fact]
    public async Task Without_a_booking_nobody_can_join()
    {
        using var t = new TestDb(); var me = t.AddUser("U1"); var admin = t.AddAdmin("ADM");
        var svc = Make(t);
        Assert.Equal("no_booking", (await svc.JoinAsync(me.Id, "kalosi", null, default)).Error);
        Assert.Equal("no_booking", (await svc.JoinAsync(admin.Id, "kalosi", "123456", default)).Error);
    }

    [Fact]
    public async Task Invited_people_may_join_from_ten_minutes_before_the_start()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(9), 60);
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(11), 60, "SJA Hall · Toraja");
        var svc = Make(t);
        Assert.Null((await svc.JoinAsync(org.Id, "kalosi", null, default)).Error);
        Assert.Equal("no_booking", (await svc.JoinAsync(org.Id, "toraja", null, default)).Error);
    }

    // ---------------------------------------------------------------- RoomAccess (lock window)

    [Fact]
    public async Task Lock_window_opens_ten_minutes_early_and_lasts_while_the_room_is_occupied()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var x = t.AddUser("X1");
        var start = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var id = await BookAsync(t, org.Id, start, 60);

        var before = await RoomAccess.ForAsync(t.Verify(), "kalosi", x.Id, start.AddMinutes(-10).AddSeconds(-1), false, default);
        Assert.Equal("free", before.Role);
        Assert.Equal(id, before.Next!.MeetingId);

        var early = await RoomAccess.ForAsync(t.Verify(), "kalosi", org.Id, start.AddMinutes(-10), false, default);
        Assert.Equal(("organizer", id), (early.Role, early.Current!.MeetingId));
        Assert.Null(early.Next);

        Assert.Equal("free", (await RoomAccess.ForAsync(t.Verify(), "kalosi", x.Id, start.AddMinutes(61), false, default)).Role);
        var overrun = await RoomAccess.ForAsync(t.Verify(), "kalosi", x.Id, start.AddMinutes(61), true, default);
        Assert.Equal(("outsider", id), (overrun.Role, overrun.Current!.MeetingId));
    }

    [Fact]
    public async Task When_the_next_booking_window_starts_it_takes_the_room_over()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var b = t.AddUser("B1");
        var start = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var idA = await BookAsync(t, a.Id, start, 60);
        var idB = await BookAsync(t, b.Id, start.AddMinutes(60), 60);

        // 9:49: still A's; B is next.
        var s1 = await RoomAccess.ForAsync(t.Verify(), "kalosi", b.Id, start.AddMinutes(49), true, default);
        Assert.Equal(("outsider", idA, idB), (s1.Role, s1.Current!.MeetingId, s1.Next!.MeetingId));
        // 9:50: B's window has started while A runs to 10:00 → B is current, A's organizer is an outsider now.
        var s2 = await RoomAccess.ForAsync(t.Verify(), "kalosi", b.Id, start.AddMinutes(50), true, default);
        Assert.Equal(("organizer", idB), (s2.Role, s2.Current!.MeetingId));
        Assert.Equal("outsider", (await RoomAccess.ForAsync(t.Verify(), "kalosi", a.Id, start.AddMinutes(50), true, default)).Role);
    }

    [Fact]
    public async Task Cancelled_bookings_and_other_rooms_do_not_count()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var start = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var id = await BookAsync(t, org.Id, start, 60);
        Assert.Equal("free", (await RoomAccess.ForAsync(t.Verify(), "toraja", org.Id, start.AddMinutes(5), false, default)).Role);
        await MeetingEndpoints.ApplyCancelAsync(t.Verify(), id, org.Id, default);
        Assert.Equal("free", (await RoomAccess.ForAsync(t.Verify(), "kalosi", org.Id, start.AddMinutes(5), false, default)).Role);
    }

    // ---------------------------------------------------------------- use now

    [Fact]
    public async Task Use_now_books_the_room_from_this_minute_with_a_password_and_the_caller_can_join()
    {
        using var t = new TestDb(); var me = t.AddUser("U1"); var friend = t.AddUser("F1");
        var svc = Make(t);
        var before = DateTime.UtcNow;
        var r = await svc.UseNowAsync(me.Id, "kalosi", 60, null, ["F1"], default);
        Assert.Null(r.Error);

        var m = t.Verify().Meetings.Include(x => x.Attendees).Single(x => x.Id == r.MeetingId);
        Assert.Equal(("Kalosi meeting", "SJA Hall · Kalosi", me.Id), (m.Title, m.Location, m.OrganizerId));
        Assert.Equal((0, 0), (m.StartAt.Second, m.StartAt.Millisecond));
        Assert.InRange(m.StartAt, before.AddMinutes(-1), DateTime.UtcNow);
        Assert.Equal(TimeSpan.FromMinutes(60), m.EndAt - m.StartAt);
        Assert.Matches("^[0-9]{6}$", m.RoomPassword);
        Assert.Contains(m.Attendees, a => a.UserId == friend.Id);
        Assert.Single(t.Verify().CalendarEvents.Where(e => e.MeetingId == m.Id));

        Assert.Null((await svc.JoinAsync(me.Id, "kalosi", null, default)).Error);
        Assert.Null((await svc.JoinAsync(friend.Id, "kalosi", null, default)).Error);
    }

    [Fact]
    public async Task Use_now_never_overlaps_the_next_booking_window()
    {
        using var t = new TestDb(); var me = t.AddUser("U1"); var org = t.AddUser("O1");
        var svc = Make(t);
        // Review Focus 4: next booking in 20 minutes → its window opens in 10, too short to offer.
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(20), 30);
        var r = await svc.UseNowAsync(me.Id, "kalosi", 60, "Quick", [], default);
        Assert.Equal("room_booked", r.Error);
        Assert.True(r.MaxMinutes is null or <= 20);
        Assert.Null(r.MaxMinutes);

        // Next booking in 45 minutes → at most 35 (until its window opens) is offered, and that fits.
        var next = DateTime.UtcNow.AddMinutes(45);
        await BookAsync(t, org.Id, next, 30, "SJA Hall · Toraja");
        var r2 = await svc.UseNowAsync(me.Id, "toraja", 60, "Quick", [], default);
        Assert.Equal("room_booked", r2.Error);
        Assert.InRange(r2.MaxMinutes!.Value, 15, 35);
        // 40 minutes would end inside the next booking's early-join window (not its StartAt): still refused.
        Assert.Equal("room_booked", (await svc.UseNowAsync(me.Id, "toraja", 40, "Quick", [], default)).Error);
        var ok = await svc.UseNowAsync(me.Id, "toraja", r2.MaxMinutes.Value, "Quick", [], default);
        Assert.Null(ok.Error);
        var mine = t.Verify().Meetings.Single(x => x.Id == ok.MeetingId);
        Assert.True(mine.EndAt <= next.AddMinutes(-10));
    }

    [Fact]
    public async Task Use_now_on_a_booked_or_unknown_room_is_refused()
    {
        using var t = new TestDb(); var me = t.AddUser("U1"); var org = t.AddUser("O1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60);
        var svc = Make(t);
        var r = await svc.UseNowAsync(me.Id, "kalosi", 30, null, [], default);
        Assert.Equal(("room_booked", (int?)null), (r.Error, r.MaxMinutes));
        Assert.Equal("unknown_room", (await svc.UseNowAsync(me.Id, "gayo", 30, null, [], default)).Error);
        Assert.Equal("invalid_minutes", (await svc.UseNowAsync(me.Id, "robusta", 5, null, [], default)).Error);
        Assert.Equal("invalid_minutes", (await svc.UseNowAsync(me.Id, "robusta", 600, null, [], default)).Error);
    }

    [Fact]
    public async Task Use_now_after_an_overrunning_booking_waits_until_the_room_is_empty()
    {
        using var t = new TestDb(); var me = t.AddUser("U1"); var org = t.AddUser("O1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-70), 60);
        var lk = new FakeRoomAdmin(); lk.Fill("kalosi", 2);
        Assert.Equal("room_booked", (await Make(t, lk).UseNowAsync(me.Id, "kalosi", 30, null, [], default)).Error);
        lk.Fill("kalosi", 0);
        Assert.Null((await Make(t, lk).UseNowAsync(me.Id, "kalosi", 30, null, [], default)).Error);
    }

    // ---------------------------------------------------------------- privacy

    [Fact]
    public async Task Password_is_never_returned_to_outsiders()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var inv = t.AddUser("I1"); var no = t.AddUser("N1"); var x = t.AddUser("X1");
        var id = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60, attendees: [inv.Id, no.Id]);
        Decline(t, id, no.Id);
        var pw = PasswordOf(t, id);
        var svc = Make(t);

        foreach (var who in new[] { x.Id, no.Id })
        {
            Assert.DoesNotContain(pw, JsonSerializer.Serialize(await svc.ListAsync(who, default)));
            Assert.DoesNotContain(pw, JsonSerializer.Serialize(await svc.JoinAsync(who, "kalosi", null, default)));
            Assert.DoesNotContain(pw, JsonSerializer.Serialize(await svc.JoinAsync(who, "kalosi", "000000" == pw ? "111111" : "000000", default)));
        }
        Assert.DoesNotContain(pw, JsonSerializer.Serialize(await svc.JoinAsync(inv.Id, "kalosi", null, default)));

        // Meeting detail: organizer and invitees see it, a declined attendee does not.
        Assert.Equal(pw, (await MeetingEndpoints.LoadUpcomingAsync(t.Verify(), org.Id, DateTime.UtcNow, default)).Single().RoomPassword);
        Assert.Equal(pw, (await MeetingEndpoints.LoadUpcomingAsync(t.Verify(), inv.Id, DateTime.UtcNow, default)).Single().RoomPassword);
        Assert.Null((await MeetingEndpoints.LoadUpcomingAsync(t.Verify(), no.Id, DateTime.UtcNow, default)).Single().RoomPassword);
    }

    [Fact]
    public void Password_limiter_counts_per_user_and_booking()
    {
        var lim = new RoomPasswordLimiter();
        var u = Guid.NewGuid(); var m1 = Guid.NewGuid(); var m2 = Guid.NewGuid();
        for (var i = 0; i < RoomPasswordLimiter.MaxAttempts; i++) { Assert.False(lim.IsBlocked(u, m1)); lim.Fail(u, m1); }
        Assert.True(lim.IsBlocked(u, m1));
        Assert.False(lim.IsBlocked(u, m2));
        Assert.False(lim.IsBlocked(Guid.NewGuid(), m1));
    }

    [Fact]
    public void Generated_passwords_are_six_digits_and_compare_ignoring_spaces()
    {
        for (var i = 0; i < 200; i++) Assert.Matches("^[0-9]{6}$", RoomPasswords.New());
        Assert.True(RoomPasswords.Matches("012345", " 012 345 "));
        Assert.False(RoomPasswords.Matches("012345", "12345"));
        Assert.False(RoomPasswords.Matches(null, "012345"));
        Assert.False(RoomPasswords.Matches("012345", null));
    }
}
