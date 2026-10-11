using IPop.Application.Calls;
using IPop.Application.MeetingRooms;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Settings;
using Xunit;

namespace IPop.Tests;

public class MeetingRoomServiceTests
{
    internal sealed class FakeRoomAdmin : ILiveKitRoomAdmin
    {
        public bool IsConfigured { get; set; } = true;
        public bool Throws { get; set; }
        public string? ThrowFor { get; set; }
        public Dictionary<string, List<LiveKitParticipant>> Rooms { get; } = new();
        public List<(string Room, int Max)> Created { get; } = [];
        public List<(string Room, string Identity, string Sources)> SourceChanges { get; } = [];
        public Task<IReadOnlyList<LiveKitParticipant>> ListAsync(string room, CancellationToken ct) =>
            Throws ? throw new HttpRequestException("down") : Task.FromResult<IReadOnlyList<LiveKitParticipant>>(Rooms.GetValueOrDefault(room) ?? []);
        public Task<bool> MuteAsync(string room, string identity, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> RemoveAsync(string room, string identity, CancellationToken ct) => Task.FromResult(true);
        public Task CreateRoomAsync(string room, int max, CancellationToken ct) { Created.Add((room, max)); return Task.CompletedTask; }
        public Task UpdateRoomMetadataAsync(string room, string metadata, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> SetSourcesAsync(string room, string identity, IReadOnlyList<string> sources, CancellationToken ct)
        {
            if (identity == ThrowFor) throw new HttpRequestException("participant update failed");
            SourceChanges.Add((room, identity, string.Join(",", sources)));
            return Task.FromResult(true);
        }
        public void Fill(string key, int count, string? plus = null)
        {
            var list = Enumerable.Range(0, count).Select(i => new LiveKitParticipant(Guid.NewGuid().ToString(), $"P{i}", [], new Dictionary<string, string>())).ToList();
            if (plus is not null) list.Add(new LiveKitParticipant(plus, "Me", [], new Dictionary<string, string>()));
            Rooms[MeetingRoomKeys.LiveKitRoom(key)] = list;
        }
    }

    internal sealed class FakeTokens : ILiveKitTokenService
    {
        public bool IsConfigured { get; set; } = true;
        public string? ServerUrl => "wss://lk.test";
        public string? LastSources;
        public string GenerateToken(string roomName, string identity, string name, TimeSpan? ttl = null, IReadOnlyList<string>? sources = null, bool canUpdateOwnMetadata = false)
        { LastSources = sources is null ? null : string.Join(",", sources); return $"tok:{roomName}:{identity}"; }
        public string GenerateAdminToken(string? room, TimeSpan? ttl = null, bool record = false) => "admin";
    }

    private static MeetingRoomService Make(TestDb t, FakeRoomAdmin lk, FakeTokens? tokens = null) =>
        new(t.Db, lk, tokens ?? new FakeTokens(), new DbSettings(t.Db));

    [Fact]
    public async Task Join_mints_a_token_for_the_hall_room_with_camera_when_video_is_allowed()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var lk = new FakeRoomAdmin(); var tokens = new FakeTokens();
        var r = await Make(t, lk, tokens).JoinAsync(me.Id, "kalosi", default);

        Assert.Null(r.Error);
        Assert.Equal(("hall-kalosi", me.Id.ToString(), true), (r.Join!.LiveKitRoom, r.Join.Identity, r.Join.VideoAllowed));
        Assert.Contains("camera", tokens.LastSources);
        Assert.Equal(("hall-kalosi", 21), lk.Created.Single());
    }

    [Fact]
    public async Task Video_off_room_gets_no_camera_source()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var lk = new FakeRoomAdmin(); var tokens = new FakeTokens(); var svc = Make(t, lk, tokens);
        await svc.SetVideoAsync("kalosi", false, "admin", default);
        var r = await svc.JoinAsync(me.Id, "kalosi", default);
        Assert.False(r.Join!.VideoAllowed);
        Assert.DoesNotContain("camera", tokens.LastSources);
    }

    [Fact]
    public async Task Twenty_first_person_is_refused_with_rooms_that_have_space()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var lk = new FakeRoomAdmin(); lk.Fill("kapal-api", 20); lk.Fill("robusta", 20);
        var r = await Make(t, lk).JoinAsync(me.Id, "kapal-api", default);
        Assert.Equal("room_full", r.Error);
        Assert.Equal(["arabika", "kalosi", "toraja"], r.Suggestions);
    }

    [Fact]
    public async Task Rejoining_myself_in_a_full_room_is_not_refused()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var lk = new FakeRoomAdmin(); lk.Fill("kapal-api", 19, plus: me.Id.ToString());
        Assert.Null((await Make(t, lk).JoinAsync(me.Id, "kapal-api", default)).Error);
    }

    [Fact]
    public async Task Unknown_room_and_unconfigured_media_are_errors_not_exceptions()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        Assert.Equal("unknown_room", (await Make(t, new FakeRoomAdmin()).JoinAsync(me.Id, "gayo", default)).Error);
        Assert.Equal("media_unavailable", (await Make(t, new FakeRoomAdmin(), new FakeTokens { IsConfigured = false }).JoinAsync(me.Id, "kalosi", default)).Error);
        Assert.Equal("media_unavailable", (await Make(t, new FakeRoomAdmin { Throws = true }).JoinAsync(me.Id, "kalosi", default)).Error);
    }

    [Fact]
    public async Task List_shows_people_sharing_and_pc_recording_and_survives_livekit_down()
    {
        using var t = new TestDb();
        var lk = new FakeRoomAdmin();
        lk.Rooms["hall-arabika"] = [new("u1", "Budi", [new LiveKitTrack("S", "SCREEN_SHARE", false)], new Dictionary<string, string> { ["rec"] = "pc" })];
        var rooms = await Make(t, lk).ListAsync(default);
        Assert.Equal(5, rooms.Count);
        var arabika = rooms.Single(r => r.Key == "arabika");
        Assert.Equal(new MeetingRoomPerson(Guid.Empty, "Budi", true, true) with { UserId = arabika.People[0].UserId }, arabika.People[0]);

        lk.Throws = true;
        var down = await Make(t, lk).ListAsync(default);
        Assert.All(down, r => Assert.False(r.Available));
    }

    [Fact]
    public async Task Turning_video_off_continues_after_one_participant_update_fails()
    {
        using var t = new TestDb();
        var lk = new FakeRoomAdmin(); lk.Fill("toraja", 2);
        var people = lk.Rooms["hall-toraja"];
        lk.ThrowFor = people[0].Identity;
        var svc = Make(t, lk);

        await svc.SetVideoAsync("toraja", false, "admin", default);

        var change = Assert.Single(lk.SourceChanges);
        Assert.Equal(("hall-toraja", people[1].Identity), (change.Room, change.Identity));
        Assert.DoesNotContain("camera", change.Sources);
        Assert.False((await svc.VideoAsync(default))["toraja"]);
    }

    [Fact]
    public async Task Turning_video_off_strips_camera_from_everyone_in_the_room()
    {
        using var t = new TestDb();
        var lk = new FakeRoomAdmin(); lk.Fill("toraja", 2);
        await Make(t, lk).SetVideoAsync("toraja", false, "admin", default);
        Assert.Equal(2, lk.SourceChanges.Count);
        Assert.All(lk.SourceChanges, c => Assert.DoesNotContain("camera", c.Sources));
        Assert.False((await Make(t, lk).VideoAsync(default))["toraja"]);
    }
}
