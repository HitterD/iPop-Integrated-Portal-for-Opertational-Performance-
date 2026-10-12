using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Api.MeetingRooms;
using IPop.Application.MeetingRooms;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
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
    private static MeetingRoomService Make(TestDb t, FakeRoomAdmin? lk = null) =>
        new(t.Db, lk ?? new FakeRoomAdmin(), new FakeTokens(), new DbSettings(t.Db), new RoomPasswordLimiter(), new RoomMeetingCreator(t.Db));

    private static async Task<Guid> BookAsync(TestDb t, Guid organizer, DateTime start, int minutes, string room = "SJA Hall · Kalosi",
        params Guid[] attendees)
    {
        var (r, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Verify(), organizer,
            new("Weekly sync", start, start.AddMinutes(minutes), null, room, null, attendees, null, 15, null), default);
        Assert.Equal(MeetingEndpoints.CreateResult.Created, r);
        return id!.Value;
    }

    private static ServiceProvider Services(TestDb t, FakeRoomAdmin lk)
    {
        var sc = new ServiceCollection();
        sc.AddScoped<IPopDbContext>(_ => t.Verify());
        sc.AddSingleton<ILiveKitRoomAdmin>(lk);
        return sc.BuildServiceProvider();
    }

    private static Task<DateTime?> TickAsync(TestDb t, FakeRoomAdmin lk, DateTime now)
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
    public async Task Card_keeps_an_overrunning_booking_while_the_room_is_occupied()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        await BookAsync(t, org.Id, DateTime.UtcNow.AddMinutes(-40), 30);
        var lk = new FakeRoomAdmin();
        Assert.Equal(MeetingRoomAccess.Free, (await Make(t, lk).ListAsync(org.Id, default)).Single(r => r.Key == "kalosi").Access);
        lk.Put("kalosi", org.Id.ToString());
        var card = (await Make(t, lk).ListAsync(org.Id, default)).Single(r => r.Key == "kalosi");
        Assert.Equal(MeetingRoomAccess.Invited, card.Access);
        Assert.NotNull(card.Now);
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
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

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
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.Equal("Weekly sync", (await recs.StartAsync(org.Id, "kalosi", default)).Recording!.Title);
    }
}
