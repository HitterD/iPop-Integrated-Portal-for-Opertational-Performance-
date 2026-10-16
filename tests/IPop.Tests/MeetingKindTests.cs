using IPop.Api.Endpoints;
using IPop.Api.MeetingRooms;
using IPop.Core.Entities;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Settings;
using IPop.Web.Pages;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Api.Endpoints.CalendarEndpoints;
using static IPop.Tests.MeetingRoomServiceTests;

namespace IPop.Tests;

/// Meeting scheduling v3 §1: Meeting.Kind (voice | room | inperson) is the explicit truth for meeting semantics and is
/// returned by every meeting/calendar DTO, so clients stop sniffing Location strings.
public class MeetingKindTests
{
    // ---------------------------------------------------------------- the one inference rule (backfill + fallback)

    [Theory]
    [InlineData("SJA Hall · Kalosi", false, "room")]
    [InlineData("SJA Hall · Kalosi", true, "room")]
    [InlineData("sja hall toraja", true, "room")]
    [InlineData("Online · iPop Meeting", false, "voice")]
    [InlineData("Online · iPop Meeting", true, "voice")]
    [InlineData(null, true, "voice")]
    [InlineData("   ", true, "voice")]
    [InlineData(null, false, null)]
    [InlineData("", false, null)]
    [InlineData("ICT office", false, "inperson")]
    [InlineData("ICT office", true, "inperson")]
    public void Infer_follows_the_backfill_rule(string? location, bool hasAttendees, string? kind) =>
        Assert.Equal(kind, MeetingKind.Infer(location, hasAttendees));

    [Theory]
    [InlineData("voice", true)]
    [InlineData("room", true)]
    [InlineData("inperson", true)]
    [InlineData("Voice", false)]
    [InlineData("video", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_accepts_only_the_three_kinds(string? kind, bool ok) => Assert.Equal(ok, MeetingKind.IsValid(kind));

    // ---------------------------------------------------------------- /api/meetings create + update

    private static MeetingEndpoints.CreateMeetingRequest Create(string? location, string? kind, string? link = null)
    {
        var s = DateTime.UtcNow.AddHours(3);
        return new("Sync", s, s.AddHours(1), null, location, link, null, null, null, null, Kind: kind);
    }

    [Fact]
    public async Task Create_voice_forces_the_online_location_and_has_no_password()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var (r, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Create("SJA Hall · Kalosi", "voice", "https://zoom.us/x"), default);
        Assert.Equal(MeetingEndpoints.CreateResult.Created, r);
        var m = await t.Verify().Meetings.SingleAsync(x => x.Id == id);
        Assert.Equal((MeetingKind.Voice, MeetingKind.OnlineLocation, (string?)null, (string?)null), (m.Kind, m.Location, m.Link, m.RoomPassword));
    }

    [Fact]
    public async Task Create_room_without_a_hall_location_is_refused_and_unknown_kinds_too()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        Assert.Equal(MeetingEndpoints.CreateResult.RoomRequired, (await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Create("ICT office", "room"), default)).Result);
        Assert.Equal(MeetingEndpoints.CreateResult.RoomRequired, (await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Create(null, "room"), default)).Result);
        Assert.Equal(MeetingEndpoints.CreateResult.InvalidKind, (await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Create(null, "video"), default)).Result);
        Assert.Empty(await t.Verify().Meetings.ToListAsync());
    }

    [Fact]
    public async Task Create_room_gets_a_password_and_inperson_keeps_its_text()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var (_, roomId, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Create("sja hall kalosi", "room"), default);
        var (_, placeId, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Create("ICT office", "inperson"), default);
        var room = await t.Verify().Meetings.SingleAsync(x => x.Id == roomId);
        var place = await t.Verify().Meetings.SingleAsync(x => x.Id == placeId);
        Assert.Equal((MeetingKind.Room, "SJA Hall · Kalosi"), (room.Kind, room.Location));
        Assert.Matches("^[0-9]{6}$", room.RoomPassword);
        Assert.Equal((MeetingKind.InPerson, "ICT office", (string?)null), (place.Kind, place.Location, place.RoomPassword));
    }

    [Fact]
    public async Task Create_without_a_kind_infers_it_from_the_location()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var (_, hall, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Create("SJA Hall · Toraja", null), default);
        var (_, online, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Create(null, null), default);
        var (_, office, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Create("ICT office", null), default);
        var kinds = await t.Verify().Meetings.ToDictionaryAsync(m => m.Id, m => m.Kind);
        // A meeting always has its organizer as attendee, so an empty place infers voice.
        Assert.Equal((MeetingKind.Room, MeetingKind.Voice, MeetingKind.InPerson), (kinds[hall!.Value], kinds[online!.Value], kinds[office!.Value]));
    }

    [Fact]
    public async Task Update_room_to_voice_clears_the_password_and_voice_to_room_makes_one()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var (_, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Create("SJA Hall · Kalosi", "room"), default);
        var m = await t.Verify().Meetings.SingleAsync(x => x.Id == id);

        var (r, _, _) = await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), m.Id, a.Id,
            new("Sync", m.StartAt, m.EndAt, "SJA Hall · Kalosi", null, null, null, null, Kind: "voice"), default);
        Assert.Equal(MeetingEndpoints.UpdateResult.Applied, r);
        var voice = await t.Verify().Meetings.SingleAsync(x => x.Id == id);
        Assert.Equal((MeetingKind.Voice, MeetingKind.OnlineLocation, (string?)null), (voice.Kind, voice.Location, voice.RoomPassword));

        Assert.Equal(MeetingEndpoints.UpdateResult.RoomRequired, (await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), m.Id, a.Id,
            new("Sync", m.StartAt, m.EndAt, null, null, null, null, null, Kind: "room"), default)).Result);
        Assert.Equal(MeetingEndpoints.UpdateResult.InvalidKind, (await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), m.Id, a.Id,
            new("Sync", m.StartAt, m.EndAt, null, null, null, null, null, Kind: "zoom"), default)).Result);

        (r, _, _) = await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), m.Id, a.Id,
            new("Sync", m.StartAt, m.EndAt, "SJA Hall · Arabika", null, null, null, null, Kind: "room"), default);
        Assert.Equal(MeetingEndpoints.UpdateResult.Applied, r);
        var room = await t.Verify().Meetings.SingleAsync(x => x.Id == id);
        Assert.Equal((MeetingKind.Room, "SJA Hall · Arabika"), (room.Kind, room.Location));
        Assert.Matches("^[0-9]{6}$", room.RoomPassword);
    }

    [Fact]
    public async Task Update_without_a_kind_follows_the_new_location()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var (_, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id, Create("SJA Hall · Kalosi", "room"), default);
        var m = await t.Verify().Meetings.SingleAsync(x => x.Id == id);
        await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), m.Id, a.Id, new("Sync", m.StartAt, m.EndAt, "Lantai 3", null, null, null, null), default);
        var moved = await t.Verify().Meetings.SingleAsync(x => x.Id == id);
        Assert.Equal((MeetingKind.InPerson, (string?)null), (moved.Kind, moved.RoomPassword));
    }

    // ---------------------------------------------------------------- DTOs

    [Fact]
    public async Task Upcoming_feed_and_detail_return_kind_and_room_key()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var start = SafeBookingTime.Start;
        var (_, roomEv, _) = await CreateSeriesAsync(t.Db, a.Id, new SaveEventRequest("Room", start, start.AddHours(1), Location: "SJA Hall · Kalosi", Kind: "room"), default);
        var (_, voiceEv, _) = await CreateSeriesAsync(t.Verify(), a.Id, new SaveEventRequest("Voice", start.AddHours(2), start.AddHours(3), Kind: "voice"), default);
        var (_, plain, _) = await CreateSeriesAsync(t.Verify(), a.Id, new SaveEventRequest("Desk", start.AddHours(4), start.AddHours(5), Location: "Lantai 3"), default);
        Assert.Null(plain!.MeetingId);

        var upcoming = await MeetingEndpoints.LoadUpcomingAsync(t.Verify(), a.Id, DateTime.UtcNow, default);
        var room = upcoming.Single(m => m.Title == "Room");
        var voice = upcoming.Single(m => m.Title == "Voice");
        Assert.Equal(("room", "kalosi"), (room.Kind, room.RoomKey));
        Assert.Equal(("voice", (string?)null, MeetingKind.OnlineLocation), (voice.Kind, voice.RoomKey, voice.Location));

        var feed = await BuildFeedAsync(t.Verify(), a.Id, DateTime.UtcNow, start.AddDays(1), FeedAccess.All, default);
        var roomItem = feed.Single(i => i.Title == "Room");
        Assert.Equal(("meeting", "room", "kalosi"), (roomItem.Kind, roomItem.MeetingKind, roomItem.RoomKey));
        Assert.Equal(("voice", (string?)null), (feed.Single(i => i.Title == "Voice").MeetingKind, feed.Single(i => i.Title == "Voice").RoomKey));
        Assert.Null(feed.Single(i => i.Title == "Desk").MeetingKind);

        var detail = await LoadDetailAsync(t.Verify(), roomEv!.Id, a.Id, default);
        Assert.Equal(("room", "kalosi"), (detail!.MeetingKind, detail.RoomKey));
        var vd = await LoadDetailAsync(t.Verify(), voiceEv!.Id, a.Id, default);
        Assert.Equal(("voice", (string?)null), (vd!.MeetingKind, vd.RoomKey));
        Assert.Null((await LoadDetailAsync(t.Verify(), plain.Id, a.Id, default))!.MeetingKind);
    }

    [Fact]
    public async Task Calendar_create_validates_the_kind()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var start = DateTime.UtcNow.AddHours(2);
        Assert.Equal(SaveResult.RoomRequired, (await CreateSeriesAsync(t.Db, a.Id, new SaveEventRequest("R", start, start.AddHours(1), Location: "Lantai 3", Kind: "room"), default)).Item1);
        Assert.Equal(SaveResult.InvalidKind, (await CreateSeriesAsync(t.Db, a.Id, new SaveEventRequest("R", start, start.AddHours(1), Kind: "video"), default)).Item1);
        // An explicit kind makes a meeting even without invitees; an in-person place keeps its text.
        var (r, ev, _) = await CreateSeriesAsync(t.Db, a.Id, new SaveEventRequest("Visit", start, start.AddHours(1), Location: "Gudang 2", Kind: "inperson"), default);
        Assert.Equal(SaveResult.Ok, r);
        var m = await t.Verify().Meetings.SingleAsync(x => x.Id == ev!.MeetingId);
        Assert.Equal((MeetingKind.InPerson, "Gudang 2", (string?)null), (m.Kind, m.Location, m.RoomPassword));
    }

    [Fact]
    public async Task Calendar_edit_room_to_voice_clears_the_password()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var start = SafeBookingTime.Start;
        var (_, ev, _) = await CreateSeriesAsync(t.Db, a.Id, new SaveEventRequest("Room", start, start.AddHours(1), Location: "SJA Hall · Kalosi"), default);
        Assert.NotNull((await t.Verify().Meetings.SingleAsync()).RoomPassword);
        Assert.Equal(MeetingKind.Room, (await t.Verify().Meetings.SingleAsync()).Kind);
        var (r, _, _) = await UpdateEventAsync(t.Verify(), ev!.Id, a.Id, new SaveEventRequest("Room", start, start.AddHours(1), Location: "SJA Hall · Kalosi", Kind: "voice"), default);
        Assert.Equal(SaveResult.Ok, r);
        var m = await t.Verify().Meetings.SingleAsync();
        Assert.Equal((MeetingKind.Voice, MeetingKind.OnlineLocation, (string?)null), (m.Kind, m.Location, m.RoomPassword));
        Assert.Equal(MeetingKind.OnlineLocation, (await t.Verify().CalendarEvents.SingleAsync()).Location);
    }

    [Fact]
    public async Task Use_now_booking_is_a_room_meeting()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var svc = new MeetingRoomService(t.Db, new FakeRoomAdmin(), new FakeTokens(), new DbSettings(t.Db), new RoomPasswordLimiter(),
            new RoomMeetingCreator(t.Db), new RoomOccupancyTracker(DateTime.MinValue, TimeSpan.MaxValue));
        var r = await svc.UseNowAsync(me.Id, "kalosi", 60, null, [], default);
        Assert.Null(r.Error);
        Assert.Equal(MeetingKind.Room, (await t.Verify().Meetings.SingleAsync(x => x.Id == r.MeetingId)).Kind);
    }

    // ---------------------------------------------------------------- client fallback

    [Theory]
    [InlineData("room", "Online · iPop Meeting", false, "room")]   // server kind wins
    [InlineData("voice", "SJA Hall · Kalosi", true, "voice")]
    [InlineData(null, "SJA Hall · Kalosi", false, "room")]
    [InlineData(null, "Online · iPop Meeting", false, "voice")]
    [InlineData(null, null, true, "voice")]
    [InlineData(null, null, false, null)]
    [InlineData(null, "ICT office", true, "inperson")]
    [InlineData("bogus", "ICT office", false, "inperson")]
    public void KindOf_prefers_the_server_kind_then_falls_back_to_the_location(string? kind, string? location, bool hasPeople, string? expected) =>
        Assert.Equal(expected, CallsSchedule.KindOf(kind, location, hasPeople));
}
