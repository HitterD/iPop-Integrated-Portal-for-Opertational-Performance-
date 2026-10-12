using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Api.MeetingRooms;
using IPop.Application.MeetingRooms;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static IPop.Tests.MeetingRoomServiceTests;

namespace IPop.Tests;

/// Meeting rooms v2 §1.5–1.6: per-viewer room cards, "Extend 15 min", and the booking worker that keeps each room
/// inside its booking (removes people who are no longer allowed, announces the next booking).
public class MeetingRoomBookingTests
{
    /// One tracker per test (xunit makes a new instance per test), shared by the services and the worker like the DI
    /// singleton. "Started" long ago: a room that is occupied counts as occupied since before any booking ended, unless
    /// a test lets the worker or a service see it empty.
    private readonly RoomOccupancyTracker occ = new(DateTime.MinValue);

    private MeetingRoomService Make(TestDb t, FakeRoomAdmin? lk = null) =>
        new(t.Db, lk ?? new FakeRoomAdmin(), new FakeTokens(), new DbSettings(t.Db), new RoomPasswordLimiter(), new RoomMeetingCreator(t.Db), occ);

    private static async Task<Guid> BookAsync(TestDb t, Guid organizer, DateTime start, int minutes, string room = "SJA Hall · Kalosi",
        params Guid[] attendees)
    {
        var (r, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Verify(), organizer,
            new("Weekly sync", start, start.AddMinutes(minutes), null, room, null, attendees, null, 15, null), default);
        Assert.Equal(MeetingEndpoints.CreateResult.Created, r);
        return id!.Value;
    }

    private ServiceProvider Services(TestDb t, ILiveKitRoomAdmin lk)
    {
        var sc = new ServiceCollection();
        sc.AddScoped<IPopDbContext>(_ => t.Verify());
        sc.AddSingleton<ILiveKitRoomAdmin>(lk);
        sc.AddSingleton(occ);
        return sc.BuildServiceProvider();
    }

    private Task<DateTime?> TickAsync(TestDb t, ILiveKitRoomAdmin lk, DateTime now)
    {
        using var sp = Services(t, lk);
        return MeetingRoomBookingWorker.TickAsync(sp, now, default);
    }

    private static JsonElement LastMetadata(FakeRoomAdmin lk, string key = "kalosi") =>
        JsonDocument.Parse(lk.MetadataUpdates.Last(m => m.Room == MeetingRoomKeys.LiveKitRoom(key)).Metadata).RootElement;

    private static void Decline(TestDb t, Guid meetingId, Guid userId)
    {
        using var db = t.Verify();
        db.MeetingAttendees.Single(a => a.MeetingId == meetingId && a.UserId == userId).RsvpStatus = "decline";
        db.SaveChanges();
    }

    // ---------------------------------------------------------------- room cards

    [Fact]
    public async Task Card_access_follows_the_viewers_role()
    {
        using var t = new TestDb();
        var org = t.AddUser("O1"); var inv = t.AddUser("I1"); var guest = t.AddUser("G1"); var outsider = t.AddUser("X1"); var decl = t.AddUser("D1");
        var id = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60, attendees: [inv.Id, decl.Id]);
        Decline(t, id, decl.Id);
        var pw = t.Verify().Meetings.Single(m => m.Id == id).RoomPassword!;
        var svc = Make(t);
        Assert.Null((await svc.JoinAsync(guest.Id, "kalosi", pw, default)).Error);

        async Task<MeetingRoomDto> Card(Guid who) => (await svc.ListAsync(who, default)).Single(r => r.Key == "kalosi");
        Assert.Equal(MeetingRoomAccess.Invited, (await Card(org.Id)).Access);
        Assert.Equal(MeetingRoomAccess.Invited, (await Card(inv.Id)).Access);
        Assert.Equal(MeetingRoomAccess.Invited, (await Card(guest.Id)).Access);
        Assert.Equal(MeetingRoomAccess.Locked, (await Card(outsider.Id)).Access);
        Assert.Equal(MeetingRoomAccess.Locked, (await Card(decl.Id)).Access);
        Assert.Equal(MeetingRoomAccess.Free, (await svc.ListAsync(org.Id, default)).Single(r => r.Key == "robusta").Access);

        Assert.True((await Card(org.Id)).Now!.IsOrganizer);
        Assert.False((await Card(inv.Id)).Now!.IsOrganizer);
        // v1 privacy rule: the title is for the organizer and attendees, not for password guests or outsiders.
        Assert.Equal("Weekly sync", (await Card(inv.Id)).Now!.Title);
        Assert.Equal(MeetingRoomService.PrivateTitle, (await Card(guest.Id)).Now!.Title);
        Assert.Equal(MeetingRoomService.PrivateTitle, (await Card(outsider.Id)).Now!.Title);
        Assert.Null((await Card(outsider.Id)).Now!.OrganizerName);
    }

    [Fact]
    public async Task Card_shows_starting_inside_the_ten_minute_early_window_and_locked_for_others()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var outsider = t.AddUser("X1");
        var start = DateTime.UtcNow.AddMinutes(8);
        await BookAsync(t, org.Id, start, 30);
        var svc = Make(t);
        var mine = (await svc.ListAsync(org.Id, default)).Single(r => r.Key == "kalosi");
        Assert.Equal(MeetingRoomAccess.Starting, mine.Access);
        Assert.Equal(start, mine.Now!.StartAt, TimeSpan.FromSeconds(1));   // the real start, for "Starts HH:mm · Join early"
        Assert.Null(mine.Next);
        Assert.Equal(MeetingRoomAccess.Locked, (await svc.ListAsync(outsider.Id, default)).Single(r => r.Key == "kalosi").Access);
    }

    [Fact]
    public async Task Card_keeps_an_overrunning_booking_only_while_the_room_stays_occupied()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-40), 30);
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString());
        var card = (await Make(t, lk).ListAsync(org.Id, default)).Single(r => r.Key == "kalosi");
        Assert.Equal(MeetingRoomAccess.Invited, card.Access);
        Assert.NotNull(card.Now);
        // Seen empty once → the booking is over for good.
        lk.Put("kalosi");
        Assert.Equal(MeetingRoomAccess.Free, (await Make(t, lk).ListAsync(org.Id, default)).Single(r => r.Key == "kalosi").Access);
        lk.Put("kalosi", org.Id.ToString());
        Assert.Equal(MeetingRoomAccess.Free, (await Make(t, lk).ListAsync(org.Id, default)).Single(r => r.Key == "kalosi").Access);
    }

    // ---------------------------------------------------------------- extend

    [Fact]
    public async Task Organizer_extends_by_15_minutes_through_the_meeting_update_path()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var inv = t.AddUser("I1"); var decl = t.AddUser("D1");
        var start = DateTime.UtcNow.AddMinutes(-20);
        var id = await BookAsync(t, org.Id, start, 30, attendees: [inv.Id, decl.Id]);
        Decline(t, id, decl.Id);
        using (var db = t.Verify())
        {
            db.MeetingAttendees.Single(a => a.MeetingId == id && a.UserId == inv.Id).RsvpStatus = "accept";
            db.SaveChanges();
        }
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString());

        var (meetingId, endAt, error) = await Make(t, lk).ExtendAsync(org.Id, "kalosi", default);

        Assert.Null(error);
        Assert.Equal(id, meetingId);
        var m = t.Verify().Meetings.Include(x => x.Attendees).Single(x => x.Id == id);
        Assert.Equal(start.AddMinutes(45), m.EndAt, TimeSpan.FromSeconds(1));
        Assert.Equal(m.EndAt, endAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal("Weekly sync", m.Title);
        Assert.Equal("SJA Hall · Kalosi", m.Location);
        Assert.Equal(3, m.Attendees.Count);
        // An extension is not a new time to answer: a decline stays a decline (so that person stays out of the room).
        Assert.Equal("accept", m.Attendees.Single(a => a.UserId == inv.Id).RsvpStatus);
        Assert.Equal("decline", m.Attendees.Single(a => a.UserId == decl.Id).RsvpStatus);
        Assert.All(t.Verify().CalendarEvents.Where(e => e.MeetingId == id), e => Assert.Equal(m.EndAt, e.EndAt));
        // The room hears about the new end at once.
        Assert.Equal(m.EndAt, LastMetadata(lk).GetProperty("booking").GetProperty("endAt").GetDateTime().ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Only_the_organizer_may_extend_and_only_a_current_booking()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var inv = t.AddUser("I1");
        var svc = Make(t);
        Assert.Equal("no_booking", (await svc.ExtendAsync(org.Id, "kalosi", default)).Error);
        Assert.Equal("unknown_room", (await svc.ExtendAsync(org.Id, "nope", default)).Error);
        var id = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 30, attendees: inv.Id);
        Assert.Equal("forbidden", (await svc.ExtendAsync(inv.Id, "kalosi", default)).Error);
        Assert.Equal(DateTime.UtcNow.AddMinutes(25), t.Verify().Meetings.Single(m => m.Id == id).EndAt, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Extend_is_refused_when_it_would_reach_the_next_bookings_lock_window()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var other = t.AddUser("P1");
        var start = DateTime.UtcNow.AddMinutes(-20);
        var id = await BookAsync(t, org.Id, start, 30);                    // ends in 10 min
        await BookAsync(t, other.Id, start.AddMinutes(30 + 24), 30);       // window opens at end + 14 min
        var svc = Make(t);

        var (_, endAt, error) = await svc.ExtendAsync(org.Id, "kalosi", default);
        Assert.Equal(("room_booked", (DateTime?)null), (error, endAt));
        Assert.Equal(start.AddMinutes(30), t.Verify().Meetings.Single(m => m.Id == id).EndAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Extend_up_to_exactly_the_next_lock_window_is_allowed()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var other = t.AddUser("P1");
        var start = DateTime.UtcNow.AddMinutes(-20);
        await BookAsync(t, org.Id, start, 30);
        await BookAsync(t, other.Id, start.AddMinutes(30 + 25), 30);       // window opens at end + 15 min
        Assert.Null((await Make(t).ExtendAsync(org.Id, "kalosi", default)).Error);
    }

    // ---------------------------------------------------------------- booking worker

    [Fact]
    public async Task Worker_removes_people_who_are_in_a_room_without_a_booking_but_never_the_recorder()
    {
        using var t = new TestDb(); var x = t.AddUser("X1");
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", x.Id.ToString(), "EG_abc", "not-a-guid");   // e.g. an old token reused after the booking
        await TickAsync(t, lk, DateTime.UtcNow);
        Assert.Equal([x.Id.ToString(), "not-a-guid"], lk.Removed.Select(r => r.Identity).OrderBy(i => i == "not-a-guid").ToList());
        Assert.DoesNotContain(lk.Removed, r => r.Identity.StartsWith("EG_"));
    }

    [Fact]
    public async Task Worker_removes_outsiders_and_keeps_organizer_attendees_and_guests()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var inv = t.AddUser("I1"); var guest = t.AddUser("G1"); var x = t.AddUser("X1");
        var id = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60, attendees: inv.Id);
        var pw = t.Verify().Meetings.Single(m => m.Id == id).RoomPassword!;
        Assert.Null((await Make(t).JoinAsync(guest.Id, "kalosi", pw, default)).Error);
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString(), inv.Id.ToString(), guest.Id.ToString(), x.Id.ToString());

        await TickAsync(t, lk, DateTime.UtcNow);

        Assert.Equal([("hall-kalosi", x.Id.ToString())], lk.Removed);
    }

    [Fact]
    public async Task Review_focus_1_attendee_removed_from_the_booking_is_moved_out_on_the_next_tick_and_needs_the_password()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var inv = t.AddUser("I1"); var stay = t.AddUser("S1");
        var start = DateTime.UtcNow.AddMinutes(-5);
        var id = await BookAsync(t, org.Id, start, 60, attendees: [inv.Id, stay.Id]);
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString(), inv.Id.ToString(), stay.Id.ToString());
        Assert.Null((await Make(t, lk).JoinAsync(inv.Id, "kalosi", null, default)).Error);
        await TickAsync(t, lk, DateTime.UtcNow);
        Assert.Empty(lk.Removed);

        var upd = new MeetingEndpoints.UpdateMeetingRequest("Weekly sync", start, start.AddMinutes(60), "SJA Hall · Kalosi", null, null,
            [t.Verify().Users.Single(u => u.Id == stay.Id).Nik], null);
        Assert.Equal(MeetingEndpoints.UpdateResult.Applied, (await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), id, org.Id, upd, default)).Result);
        await TickAsync(t, lk, DateTime.UtcNow);

        Assert.Equal([("hall-kalosi", inv.Id.ToString())], lk.Removed);
        Assert.Equal("password_required", (await Make(t, lk).JoinAsync(inv.Id, "kalosi", null, default)).Error);
    }

    [Fact]
    public async Task Declining_while_inside_moves_the_person_out_on_the_next_tick()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var inv = t.AddUser("I1");
        var id = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60, attendees: inv.Id);
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString(), inv.Id.ToString());
        Decline(t, id, inv.Id);
        await TickAsync(t, lk, DateTime.UtcNow);
        Assert.Equal([("hall-kalosi", inv.Id.ToString())], lk.Removed);
    }

    [Fact]
    public async Task Review_focus_2_next_booking_warns_one_minute_ahead_then_removes_only_people_not_allowed_in_it()
    {
        using var t = new TestDb();
        var a = t.AddUser("A1"); var aOnly = t.AddUser("A2"); var both = t.AddUser("AB"); var b = t.AddUser("B1"); var bInv = t.AddUser("B2");
        var aStart = DateTime.UtcNow.AddMinutes(-30);
        var aId = await BookAsync(t, a.Id, aStart, 60, attendees: [aOnly.Id, both.Id]);          // A ends at +30
        var bStart = aStart.AddMinutes(60);                                                      // B starts as A ends …
        var bId = await BookAsync(t, b.Id, bStart, 30, attendees: [both.Id, bInv.Id]);           // … window opens 10 min before
        var takeover = bStart.AddMinutes(-10);
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", a.Id.ToString(), aOnly.Id.ToString(), both.Id.ToString());

        // Long before: nobody leaves, the metadata announces the takeover, no leaving list yet; the worker asks to wake
        // at the warning moment.
        var due = await TickAsync(t, lk, takeover.AddMinutes(-5));
        Assert.Empty(lk.Removed);
        var booking = LastMetadata(lk).GetProperty("booking");
        Assert.Equal(aId, booking.GetProperty("meetingId").GetGuid());
        Assert.Equal(a.Id, booking.GetProperty("organizerId").GetGuid());
        Assert.Equal(aStart.AddMinutes(60), booking.GetProperty("endAt").GetDateTime().ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.Equal(bStart, booking.GetProperty("nextStartAt").GetDateTime().ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.Equal(takeover, booking.GetProperty("takeoverAt").GetDateTime().ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.Equal(JsonValueKind.Null, booking.GetProperty("leaving").ValueKind);
        Assert.Equal(takeover.AddMinutes(-1), due!.Value, TimeSpan.FromSeconds(1));

        // One minute ahead: still nobody removed, but exactly the people who must go are warned.
        await TickAsync(t, lk, takeover.AddSeconds(-50));
        Assert.Empty(lk.Removed);
        var leaving = LastMetadata(lk).GetProperty("booking").GetProperty("leaving").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { a.Id.ToString(), aOnly.Id.ToString() }.Order(), leaving.Order());

        // B's invitee may already come in early (window started) — and at the takeover A's people not in B leave.
        lk.Rooms["hall-kalosi"].Add(new LiveKitParticipant(bInv.Id.ToString(), "B2", [], new Dictionary<string, string>()));
        await TickAsync(t, lk, takeover.AddSeconds(1));
        Assert.Equal(new[] { a.Id.ToString(), aOnly.Id.ToString() }.Order(), lk.Removed.Select(r => r.Identity).Order());
        Assert.Equal(bId, LastMetadata(lk).GetProperty("booking").GetProperty("meetingId").GetGuid());
        Assert.Equal(new[] { both.Id.ToString(), bInv.Id.ToString() }.Order(), lk.Rooms["hall-kalosi"].Select(p => p.Identity).Order());
    }

    [Fact]
    public async Task After_the_end_without_a_next_booking_people_may_stay_and_once_empty_nobody_can_rejoin()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var inv = t.AddUser("I1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-40), 30, attendees: inv.Id);   // ended 10 min ago
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString(), inv.Id.ToString());

        await TickAsync(t, lk, DateTime.UtcNow);
        Assert.Empty(lk.Removed);
        Assert.Null((await Make(t, lk).JoinAsync(inv.Id, "kalosi", null, default)).Error);   // reconnect while occupied

        lk.Put("kalosi");
        Assert.Equal("no_booking", (await Make(t, lk).JoinAsync(inv.Id, "kalosi", null, default)).Error);
    }

    // ---------------------------------------------------------------- metadata merge

    [Fact]
    public async Task Booking_and_recording_metadata_are_merged_by_every_writer()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60);
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString());
        var recs = new MeetingRecordingService(t.Db, lk, new MeetingRecordingServiceTests.FakeEgress(), new DbSettings(t.Db),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), occ);

        var started = await recs.StartAsync(org.Id, "kalosi", default);
        Assert.Null(started.Error);
        var meta = LastMetadata(lk);
        Assert.True(meta.TryGetProperty("recording", out var rec) && rec.GetProperty("id").GetGuid() == started.Recording!.Id);
        Assert.True(meta.TryGetProperty("booking", out _));

        // The booking worker keeps the recording key …
        await TickAsync(t, lk, DateTime.UtcNow);
        meta = LastMetadata(lk);
        Assert.True(meta.TryGetProperty("recording", out _));
        Assert.True(meta.TryGetProperty("booking", out _));

        // … and stopping the recording keeps the booking key.
        Assert.Null((await recs.StopAsync(org.Id, started.Recording.Id, default)).Error);
        meta = LastMetadata(lk);
        Assert.False(meta.TryGetProperty("recording", out _));
        Assert.True(meta.TryGetProperty("booking", out _));
    }

    [Fact]
    public async Task Recording_title_comes_from_the_booking_inside_the_early_window()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(5), 30);
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString());
        var recs = new MeetingRecordingService(t.Db, lk, new MeetingRecordingServiceTests.FakeEgress(), new DbSettings(t.Db),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), occ);
        Assert.Equal("Weekly sync", (await recs.StartAsync(org.Id, "kalosi", default)).Recording!.Title);
    }

    // ---------------------------------------------------------------- fix round 1: overrun has a memory

    [Fact]
    public async Task Reconnect_with_a_valid_token_after_the_room_emptied_does_not_revive_the_ended_booking()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var inv = t.AddUser("I1");
        var end = DateTime.UtcNow.AddMinutes(-5);
        await BookAsync(t, org.Id, end.AddMinutes(-30), 30, attendees: inv.Id);
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString(), inv.Id.ToString());
        await TickAsync(t, lk, DateTime.UtcNow);
        Assert.Empty(lk.Removed);                                   // still overrunning, continuously occupied

        lk.Put("kalosi");                                           // everyone left
        await TickAsync(t, lk, DateTime.UtcNow);
        lk.Put("kalosi", inv.Id.ToString());                        // reconnect with a token that is still valid
        await TickAsync(t, lk, DateTime.UtcNow);

        Assert.Equal([("hall-kalosi", inv.Id.ToString())], lk.Removed);
        Assert.Equal("no_booking", (await Make(t, lk).JoinAsync(org.Id, "kalosi", null, default)).Error);
        Assert.Equal(MeetingRoomAccess.Free, (await Make(t, lk).ListAsync(org.Id, default)).Single(r => r.Key == "kalosi").Access);
    }

    [Fact]
    public async Task An_api_restart_counts_as_the_room_having_been_empty()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-40), 30);   // ended 10 min ago
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString());
        var svc = new MeetingRoomService(t.Db, lk, new FakeTokens(), new DbSettings(t.Db), new RoomPasswordLimiter(), new RoomMeetingCreator(t.Db),
            new RoomOccupancyTracker(DateTime.UtcNow));            // app started just now
        Assert.Equal(MeetingRoomAccess.Free, (await svc.ListAsync(org.Id, default)).Single(r => r.Key == "kalosi").Access);
    }

    [Fact]
    public async Task Cancelling_the_running_booking_while_people_are_inside_does_not_make_an_older_booking_current()
    {
        using var t = new TestDb(); var old = t.AddUser("O0"); var org = t.AddUser("O1");
        var earlier = await BookAsync(t, old.Id, DateTime.UtcNow.AddMinutes(-90), 30);       // ended an hour ago today
        var yesterday = await BookAsync(t, old.Id, DateTime.UtcNow.AddDays(-1), 30, "SJA Hall · Toraja");
        var now1 = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-10), 60);
        var now2 = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-10), 60, "SJA Hall · Toraja");
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString(), old.Id.ToString());
        lk.Put("toraja", org.Id.ToString(), old.Id.ToString());
        await TickAsync(t, lk, DateTime.UtcNow);

        await MeetingEndpoints.ApplyCancelAsync(t.Verify(), now1, org.Id, default);
        await MeetingEndpoints.ApplyCancelAsync(t.Verify(), now2, org.Id, default);

        foreach (var key in new[] { "kalosi", "toraja" })
        {
            Assert.Equal("no_booking", (await Make(t, lk).ExtendAsync(old.Id, key, default)).Error);
            var card = (await Make(t, lk).ListAsync(old.Id, default)).Single(r => r.Key == key);
            Assert.Equal(MeetingRoomAccess.Free, card.Access);
            Assert.Null(card.Now);
        }
        var meetings = t.Verify().Meetings.ToDictionary(m => m.Id);
        Assert.Equal(TimeSpan.FromMinutes(30), meetings[earlier].EndAt - meetings[earlier].StartAt);
        Assert.Equal(TimeSpan.FromMinutes(30), meetings[yesterday].EndAt - meetings[yesterday].StartAt);

        await TickAsync(t, lk, DateTime.UtcNow);
        Assert.Equal(4, lk.Removed.Count);                           // nobody holds either room any more
    }

    [Fact]
    public async Task Yesterdays_booking_never_holds_the_room_today()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        // 23:30–23:50 local yesterday; asked at 00:30 local today with the room continuously occupied (still < 4 h).
        var dayStart = RoomAccess.DayStartUtc(DateTime.UtcNow);
        var id = await BookAsync(t, org.Id, dayStart.AddMinutes(-30), 20);
        var at = dayStart.AddMinutes(30);
        Assert.Null((await RoomAccess.ForAsync(t.Verify(), "kalosi", org.Id, at, RoomOccupancy.Continuous, default)).Current);
        // The same overrun inside one day does count.
        Assert.Equal(id, (await RoomAccess.ForAsync(t.Verify(), "kalosi", org.Id, dayStart.AddMinutes(-5), RoomOccupancy.Continuous, default))
            .Current?.MeetingId);
    }

    [Fact]
    public async Task Overrun_is_capped_four_hours_after_the_end()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var dayStart = RoomAccess.DayStartUtc(DateTime.UtcNow);
        var start = dayStart.AddHours(2);
        var id = await BookAsync(t, org.Id, start, 60);
        var end = start.AddMinutes(60);
        var held = await RoomAccess.ForAsync(t.Verify(), "kalosi", org.Id, end.AddHours(4).AddSeconds(-1), RoomOccupancy.Continuous, default);
        Assert.Equal(id, held.Current?.MeetingId);
        Assert.Null((await RoomAccess.ForAsync(t.Verify(), "kalosi", org.Id, end.AddHours(4), RoomOccupancy.Continuous, default)).Current);
        // Occupied, but seen empty after the end → over.
        Assert.Null((await RoomAccess.ForAsync(t.Verify(), "kalosi", org.Id, end.AddMinutes(30), new RoomOccupancy(true, end.AddMinutes(1)), default))
            .Current);
        Assert.Equal(id, (await RoomAccess.ForAsync(t.Verify(), "kalosi", org.Id, end.AddMinutes(30), new RoomOccupancy(true, end.AddMinutes(-1)), default))
            .Current?.MeetingId);
    }

    [Fact]
    public async Task Organizer_can_extend_a_continuously_occupied_overrun_by_15_minutes_from_now()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var id = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-40), 30);
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString());
        var (meetingId, endAt, error) = await Make(t, lk).ExtendAsync(org.Id, "kalosi", default);
        Assert.Null(error);
        Assert.Equal(id, meetingId);
        Assert.Equal(DateTime.UtcNow.AddMinutes(15), endAt!.Value, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Extend_without_livekit_during_an_overrun_says_media_unavailable_not_no_booking()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-40), 30);
        var lk = new FakeRoomAdmin { Throws = true };
        Assert.Equal("media_unavailable", (await Make(t, lk).ExtendAsync(org.Id, "kalosi", default)).Error);
    }

    [Fact]
    public async Task Extend_of_a_booking_cancelled_meanwhile_reports_cancelled()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var id = await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 30);
        var svc = new MeetingRoomService(t.Db, new FakeRoomAdmin(), new FakeTokens(), new DbSettings(t.Db), new RoomPasswordLimiter(),
            new CancelFirst(t, id, new RoomMeetingCreator(t.Verify())), occ);
        Assert.Equal("cancelled", (await svc.ExtendAsync(org.Id, "kalosi", default)).Error);
    }

    /// Cancels the meeting just before the update runs (a cancel racing an Extend).
    private sealed class CancelFirst(TestDb t, Guid id, IRoomBookingWriter inner) : IRoomBookingWriter
    {
        public Task<Guid?> CreateAsync(Guid o, string title, DateTime s, DateTime e, string loc, IReadOnlyList<string> niks, CancellationToken ct) =>
            inner.CreateAsync(o, title, s, e, loc, niks, ct);
        public async Task<string?> SetEndAsync(Guid organizerId, Guid meetingId, DateTime endUtc, CancellationToken ct)
        {
            await MeetingEndpoints.ApplyCancelAsync(t.Verify(), id, organizerId, ct);
            return await inner.SetEndAsync(organizerId, meetingId, endUtc, ct);
        }
    }

    // ---------------------------------------------------------------- fix round 1: worker robustness

    [Fact]
    public async Task A_failure_in_one_room_does_not_skip_the_other_rooms()
    {
        using var t = new TestDb(); var x = t.AddUser("X1");
        var lk = new FailingRoom("hall-arabika");                       // arabika comes before kalosi and toraja
        lk.Put("arabika", x.Id.ToString());
        lk.Put("kalosi", x.Id.ToString());
        lk.Put("toraja", x.Id.ToString());
        await TickAsync(t, lk, DateTime.UtcNow);
        Assert.Equal(new[] { "hall-kalosi", "hall-toraja" }, lk.Removed.Select(r => r.Room).Order());
    }

    /// LiveKit that answers, but whose answer for one room makes processing blow up (not an HTTP error).
    private sealed class FailingRoom(string room) : ILiveKitRoomAdmin
    {
        private readonly FakeRoomAdmin inner = new();
        public List<(string Room, string Identity)> Removed => inner.Removed;
        public void Put(string key, params string[] ids) => inner.Put(key, ids);
        public bool IsConfigured => true;
        public Task<IReadOnlyList<LiveKitParticipant>> ListAsync(string r, CancellationToken ct) =>
            r == room ? throw new InvalidOperationException("bad data") : inner.ListAsync(r, ct);
        public Task<bool> MuteAsync(string r, string i, CancellationToken ct) => inner.MuteAsync(r, i, ct);
        public Task<bool> RemoveAsync(string r, string i, CancellationToken ct) => inner.RemoveAsync(r, i, ct);
        public Task CreateRoomAsync(string r, int m, CancellationToken ct) => inner.CreateRoomAsync(r, m, ct);
        public Task UpdateRoomMetadataAsync(string r, string m, CancellationToken ct) => inner.UpdateRoomMetadataAsync(r, m, ct);
        public Task<bool> SetSourcesAsync(string r, string i, IReadOnlyList<string> s, CancellationToken ct) => inner.SetSourcesAsync(r, i, s, ct);
    }

    [Fact]
    public async Task Worker_does_not_rewrite_unchanged_metadata()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60);
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString());
        var now = DateTime.UtcNow;
        await TickAsync(t, lk, now);
        await TickAsync(t, lk, now.AddSeconds(30));
        await TickAsync(t, lk, now.AddSeconds(60));
        Assert.Single(lk.MetadataUpdates, m => m.Room == "hall-kalosi");
        // After the room was empty (LiveKit drops it with its metadata) the next pass writes again.
        lk.Put("kalosi");
        await TickAsync(t, lk, now.AddSeconds(90));
        lk.Put("kalosi", org.Id.ToString());
        await TickAsync(t, lk, now.AddSeconds(120));
        Assert.Equal(2, lk.MetadataUpdates.Count(m => m.Room == "hall-kalosi"));
    }

    [Fact]
    public async Task Join_succeeds_even_when_the_metadata_update_blows_up()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60);
        var lk = new MetadataThrows();
        var svc = new MeetingRoomService(t.Db, lk, new FakeTokens(), new DbSettings(t.Db), new RoomPasswordLimiter(), new RoomMeetingCreator(t.Db), occ);
        var r = await svc.JoinAsync(org.Id, "kalosi", null, default);
        Assert.Null(r.Error);
        Assert.NotNull(r.Join);
    }

    private sealed class MetadataThrows : ILiveKitRoomAdmin
    {
        private readonly FakeRoomAdmin inner = new();
        public bool IsConfigured => true;
        public Task<IReadOnlyList<LiveKitParticipant>> ListAsync(string r, CancellationToken ct) => inner.ListAsync(r, ct);
        public Task<bool> MuteAsync(string r, string i, CancellationToken ct) => inner.MuteAsync(r, i, ct);
        public Task<bool> RemoveAsync(string r, string i, CancellationToken ct) => inner.RemoveAsync(r, i, ct);
        public Task CreateRoomAsync(string r, int m, CancellationToken ct) => inner.CreateRoomAsync(r, m, ct);
        public Task UpdateRoomMetadataAsync(string r, string m, CancellationToken ct) => throw new InvalidOperationException("twirp shape changed");
        public Task<bool> SetSourcesAsync(string r, string i, IReadOnlyList<string> s, CancellationToken ct) => inner.SetSourcesAsync(r, i, s, ct);
    }
}
