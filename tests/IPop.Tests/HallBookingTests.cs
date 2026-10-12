using IPop.Api.Endpoints;
using IPop.Application.Hall;
using IPop.Core.Entities;
using IPop.Infrastructure.Hall;
using Xunit;

namespace IPop.Tests;

/// SJA Hall meeting rooms are booked as meetings located "SJA Hall · {room}": one booking at a time per room, and
/// only the organizer and attendees may walk in while it runs.
public class HallBookingTests
{
    [Theory]
    [InlineData("SJA Hall · Robusta", "robusta")]
    [InlineData("sja hall - kapal api", "kapal-api")]
    [InlineData("SJA Hall  ·  Toraja ", "toraja")]
    [InlineData("SJA Hall · Gayo", null)]
    [InlineData("Ruang Rapat Lt. 2", null)]
    [InlineData(null, null)]
    public void Room_keys_come_from_forgiving_locations(string? location, string? key) => Assert.Equal(key, HallBooking.RoomKey(location));

    [Fact]
    public void Normalize_writes_one_canonical_form_and_leaves_other_text_alone()
    {
        Assert.Equal("SJA Hall · Kapal Api", HallBooking.Normalize("sja hall: KAPAL API"));
        Assert.Equal("Lantai 3", HallBooking.Normalize("Lantai 3"));
    }

    private static MeetingEndpoints.CreateMeetingRequest Req(DateTime start, int minutes, string location, params Guid[] attendees) =>
        new("Weekly sync", start, start.AddMinutes(minutes), null, location, null, attendees, null, null, null);

    [Fact]
    public async Task Overlapping_bookings_of_the_same_room_are_refused_on_create_and_update()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var b = t.AddUser("B1");
        var start = DateTime.UtcNow.AddHours(2);
        var (r1, id1, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Req(start, 60, "sja hall robusta"), default);
        Assert.Equal(MeetingEndpoints.CreateResult.Created, r1);
        Assert.Equal("SJA Hall · Robusta", t.Verify().Meetings.Single().Location);

        Assert.Equal(MeetingEndpoints.CreateResult.RoomBooked,
            (await MeetingEndpoints.ApplyCreateAsync(t.Verify(), b.Id, Req(start.AddMinutes(30), 60, "SJA Hall · Robusta"), default)).Result);
        // Back-to-back, another room, and a non-hall place are all fine.
        Assert.Equal(MeetingEndpoints.CreateResult.Created,
            (await MeetingEndpoints.ApplyCreateAsync(t.Verify(), b.Id, Req(start.AddMinutes(60), 30, "SJA Hall · Robusta"), default)).Result);
        Assert.Equal(MeetingEndpoints.CreateResult.Created,
            (await MeetingEndpoints.ApplyCreateAsync(t.Verify(), b.Id, Req(start, 60, "SJA Hall · Toraja"), default)).Result);
        var (_, idOther, _) = await MeetingEndpoints.ApplyCreateAsync(t.Verify(), b.Id, Req(start, 60, "Ruang 2"), default);

        // Moving the "Ruang 2" meeting into Robusta at the same time clashes; editing the original itself does not.
        var clash = new MeetingEndpoints.UpdateMeetingRequest("Weekly sync", start, start.AddMinutes(60), "SJA Hall · Robusta", null, null, null, null);
        Assert.Equal(MeetingEndpoints.UpdateResult.RoomBooked, (await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), idOther!.Value, b.Id, clash, default)).Result);
        Assert.Equal(MeetingEndpoints.UpdateResult.Applied, (await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), id1!.Value, a.Id, clash, default)).Result);
    }

    [Fact]
    public async Task During_a_booking_only_the_organizer_and_attendees_who_did_not_decline_may_enter()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var guest = t.AddUser("G1"); var no = t.AddUser("N1"); var other = t.AddUser("X1");
        var start = DateTime.UtcNow.AddMinutes(-10);
        var (_, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req(start, 60, "SJA Hall · Kalosi", guest.Id, no.Id), default);
        var db = t.Verify();
        db.MeetingAttendees.Single(x => x.MeetingId == id && x.UserId == no.Id).RsvpStatus = "decline";
        db.SaveChanges();

        var now = DateTime.UtcNow;
        Assert.True((await HallBookings.CurrentAsync(t.Verify(), "kalosi", org.Id, now, default))!.Allowed);
        Assert.True((await HallBookings.CurrentAsync(t.Verify(), "kalosi", guest.Id, now, default))!.Allowed);
        Assert.False((await HallBookings.CurrentAsync(t.Verify(), "kalosi", no.Id, now, default))!.Allowed);
        var seen = await HallBookings.CurrentAsync(t.Verify(), "kalosi", other.Id, now, default);
        Assert.Equal(("Weekly sync", false), (seen!.Title, seen.Allowed));
        Assert.Null(await HallBookings.CurrentAsync(t.Verify(), "robusta", other.Id, now, default));
        Assert.Null(await HallBookings.CurrentAsync(t.Verify(), "kalosi", other.Id, now.AddHours(2), default));

        // Cancelled meetings free the room.
        var m = t.Verify(); m.Meetings.Single().Status = MeetingStatus.Cancelled; m.SaveChanges();
        Assert.Null(await HallBookings.CurrentAsync(t.Verify(), "kalosi", other.Id, now, default));
    }

    [Fact]
    public async Task Next_booking_is_the_earliest_future_meeting_of_that_room_with_its_organizer()
    {
        using var t = new TestDb(); var org = t.AddUser("O2");
        var now = DateTime.UtcNow;
        await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req(now.AddHours(5), 30, "SJA Hall · Toraja"), default);
        await MeetingEndpoints.ApplyCreateAsync(t.Verify(), org.Id, Req(now.AddHours(2), 30, "SJA Hall · Toraja"), default);
        await MeetingEndpoints.ApplyCreateAsync(t.Verify(), org.Id, Req(now.AddHours(1), 30, "SJA Hall · Robusta"), default);

        var next = await HallBookings.NextAsync(t.Verify(), "toraja", org.Id, now, default);
        Assert.NotNull(next);
        Assert.Equal(now.AddHours(2), next!.StartAt, TimeSpan.FromSeconds(1));
        Assert.Equal("User O2", next.OrganizerName);
        Assert.True(next.Allowed);
        Assert.Null(await HallBookings.NextAsync(t.Verify(), "kalosi", org.Id, now, default));
    }

    [Fact]
    public void Five_rooms_are_seeded_with_capacity_20()
    {
        using var t = new TestDb();
        var rooms = t.Db.HallRooms.OrderBy(r => r.SortOrder).ToList();
        Assert.Equal(["robusta", "arabika", "kalosi", "kapal-api", "toraja"], rooms.Select(r => r.Key));
        Assert.All(rooms, r => Assert.Equal(20, r.Capacity));
    }
}
