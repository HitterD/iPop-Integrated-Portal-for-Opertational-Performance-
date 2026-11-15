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
        /// LiveKit rooms whose participant list fails (only that room).
        public HashSet<string> ThrowRooms { get; } = [];
        /// Each ListAsync waits this long first (to prove the five rooms are asked in parallel).
        public TimeSpan ListDelay { get; set; }
        private int _inFlight, _maxInFlight;
        public int MaxConcurrentLists => Volatile.Read(ref _maxInFlight);
        /// People by LiveKit room. A key "hall-{key}" is the LOGICAL hall room: any booking media room of that key
        /// ("hall-{key}-{meetingId:N}", default namespace) without an exact entry of its own sees these people. Tests that
        /// need two bookings' media rooms apart put people under the exact media room name (<see cref="Media"/>).
        public Dictionary<string, List<LiveKitParticipant>> Rooms { get; } = new();
        public List<(string Room, int Max)> Created { get; } = [];
        public List<(string Room, string Identity, string Sources)> SourceChanges { get; } = [];
        /// Every room name ListAsync was asked for.
        public List<string> Listed { get; } = [];
        public static string Media(string key, Guid meetingId) => MeetingRoomMedia.Default.Room(key, meetingId);
        /// "hall-{key}" for a media room of the default namespace (or the name itself).
        public static string Logical(string room) => MeetingRoomMedia.Default.Parse(room) is { } p ? $"hall-{p.Key}" : room;
        private List<LiveKitParticipant>? Resolve(string room) =>
            Rooms.TryGetValue(room, out var exact) ? exact : Rooms.GetValueOrDefault(Logical(room));
        public async Task<IReadOnlyList<LiveKitParticipant>> ListAsync(string room, CancellationToken ct)
        {
            lock (Listed) Listed.Add(room);
            if (Throws) throw new HttpRequestException("down");
            var now = Interlocked.Increment(ref _inFlight);
            try
            {
                int seen;
                while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
                if (ListDelay > TimeSpan.Zero) await Task.Delay(ListDelay, ct);
                if (ThrowRooms.Contains(room) || ThrowRooms.Contains(Logical(room))) throw new TaskCanceledException("LiveKit timeout");
                return Resolve(room)?.ToList() ?? [];
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
        public Task<IReadOnlyList<string>> ListRoomsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Rooms.Keys.ToList());
        public Task<bool> MuteAsync(string room, string identity, CancellationToken ct) => Task.FromResult(true);
        public List<(string Room, string Identity)> Removed { get; } = [];
        public Task<bool> RemoveAsync(string room, string identity, CancellationToken ct)
        {
            Removed.Add((room, identity));
            Resolve(room)?.RemoveAll(p => p.Identity == identity);
            return Task.FromResult(true);
        }
        /// Puts these identities in the (logical) room (replacing whoever was there).
        public void Put(string key, params string[] identities) =>
            Rooms[$"hall-{key}"] = identities.Select(i => new LiveKitParticipant(i, i, [], new Dictionary<string, string>())).ToList();
        /// Puts these identities in exactly this LiveKit room.
        public void PutIn(string room, params string[] identities) =>
            Rooms[room] = identities.Select(i => new LiveKitParticipant(i, i, [], new Dictionary<string, string>())).ToList();
        public Task CreateRoomAsync(string room, int max, CancellationToken ct) { Created.Add((room, max)); return Task.CompletedTask; }
        public List<(string Room, string Metadata)> MetadataUpdates { get; } = [];
        public Task UpdateRoomMetadataAsync(string room, string metadata, CancellationToken ct) { MetadataUpdates.Add((room, metadata)); return Task.CompletedTask; }
        public Task<bool> SetSourcesAsync(string room, string identity, IReadOnlyList<string> sources, CancellationToken ct)
        {
            if (identity == ThrowFor) throw new HttpRequestException("participant update failed");
            SourceChanges.Add((room, identity, string.Join(",", sources)));
            return Task.FromResult(true);
        }
        public List<(string Room, byte[] Payload, string Topic)> DataSent { get; } = [];
        public bool SendDataThrows { get; set; }
        public Task SendDataAsync(string room, byte[] payload, string topic, CancellationToken ct)
        {
            if (SendDataThrows) throw new HttpRequestException("send data failed");
            DataSent.Add((room, payload, topic));
            return Task.CompletedTask;
        }
        public void Fill(string key, int count, string? plus = null)
        {
            var list = Enumerable.Range(0, count).Select(i => new LiveKitParticipant(Guid.NewGuid().ToString(), $"P{i}", [], new Dictionary<string, string>())).ToList();
            if (plus is not null) list.Add(new LiveKitParticipant(plus, "Me", [], new Dictionary<string, string>()));
            Rooms[$"hall-{key}"] = list;
        }
    }

    internal sealed class FakeTokens : ILiveKitTokenService
    {
        public bool IsConfigured { get; set; } = true;
        public string? PublicUrl => "wss://lk.test";
        public string? ServerUrl => PublicUrl;
        public string? ServerApiUrl => ServerUrl;
        public string? LastSources;
        public string GenerateToken(string roomName, string identity, string name, TimeSpan? ttl = null, IReadOnlyList<string>? sources = null, bool canUpdateOwnMetadata = false)
        { LastSources = sources is null ? null : string.Join(",", sources); return $"tok:{roomName}:{identity}"; }
        public string GenerateAdminToken(string? room, TimeSpan? ttl = null, bool record = false) => "admin";
    }

    private static MeetingRoomService Make(TestDb t, FakeRoomAdmin lk, FakeTokens? tokens = null) =>
        new(t.Db, lk, tokens ?? new FakeTokens(), new DbSettings(t.Db), new RoomPasswordLimiter(), new IPop.Api.MeetingRooms.RoomMeetingCreator(t.Db));

    /// v2: rooms are booking-only, so join tests first book the room for <paramref name="me"/> (running now).
    private static Task<Guid> BookNowAsync(TestDb t, Guid me, string roomName) =>
        BookAsync(t, me, "Sync", DateTime.UtcNow.AddMinutes(-5), 60, $"SJA Hall · {roomName}");

    [Fact]
    public async Task Join_mints_a_token_for_the_hall_room_with_camera_when_video_is_allowed()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var booking = await BookNowAsync(t, me.Id, "Kalosi");
        var lk = new FakeRoomAdmin(); var tokens = new FakeTokens();
        var r = await Make(t, lk, tokens).JoinAsync(me.Id, "kalosi", null, default);

        Assert.Null(r.Error);
        Assert.Equal((FakeRoomAdmin.Media("kalosi", booking), me.Id.ToString(), true), (r.Join!.LiveKitRoom, r.Join.Identity, r.Join.VideoAllowed));
        Assert.Contains("camera", tokens.LastSources);
        Assert.Equal((FakeRoomAdmin.Media("kalosi", booking), 21), lk.Created.Single());
    }

    [Fact]
    public async Task Video_off_room_gets_no_camera_source()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        await BookNowAsync(t, me.Id, "Kalosi");
        var lk = new FakeRoomAdmin(); var tokens = new FakeTokens(); var svc = Make(t, lk, tokens);
        await svc.SetVideoAsync("kalosi", false, "admin", default);
        var r = await svc.JoinAsync(me.Id, "kalosi", null, default);
        Assert.False(r.Join!.VideoAllowed);
        Assert.DoesNotContain("camera", tokens.LastSources);
    }

    [Fact]
    public async Task Twenty_first_person_is_refused_with_rooms_that_have_space()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        await BookNowAsync(t, me.Id, "Kapal Api");
        var lk = new FakeRoomAdmin(); lk.Fill("kapal-api", 20); lk.Fill("robusta", 20);
        var r = await Make(t, lk).JoinAsync(me.Id, "kapal-api", null, default);
        Assert.Equal("room_full", r.Error);
        Assert.Equal(["robusta", "arabika", "kalosi", "toraja"], r.Suggestions); // legacy physical-room occupants are not v2 booking participants
    }

    [Fact]
    public async Task Rejoining_myself_in_a_full_room_is_not_refused()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        await BookNowAsync(t, me.Id, "Kapal Api");
        var lk = new FakeRoomAdmin(); lk.Fill("kapal-api", 19, plus: me.Id.ToString());
        Assert.Null((await Make(t, lk).JoinAsync(me.Id, "kapal-api", null, default)).Error);
    }

    [Fact]
    public async Task Unknown_room_and_unconfigured_media_are_errors_not_exceptions()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        await BookNowAsync(t, me.Id, "Kalosi");
        Assert.Equal("unknown_room", (await Make(t, new FakeRoomAdmin()).JoinAsync(me.Id, "gayo", null, default)).Error);
        Assert.Equal("media_unavailable", (await Make(t, new FakeRoomAdmin(), new FakeTokens { IsConfigured = false }).JoinAsync(me.Id, "kalosi", null, default)).Error);
        Assert.Equal("media_unavailable", (await Make(t, new FakeRoomAdmin { Throws = true }).JoinAsync(me.Id, "kalosi", null, default)).Error);
    }

    [Fact]
    public async Task List_shows_people_sharing_and_pc_recording_and_survives_livekit_down()
    {
        using var t = new TestDb();
        var lk = new FakeRoomAdmin();
        var organizer = t.AddUser("O1"); await BookNowAsync(t, organizer.Id, "Arabika");
        lk.Rooms["hall-arabika"] = [new("u1", "Budi", [new LiveKitTrack("S", "SCREEN_SHARE", false)], new Dictionary<string, string> { ["rec"] = "pc" })];
        var rooms = await Make(t, lk).ListAsync(Guid.NewGuid(), default);
        Assert.Equal(5, rooms.Count);
        var arabika = rooms.Single(r => r.Key == "arabika");
        Assert.Equal(new MeetingRoomPerson(Guid.Empty, "Budi", true, true) with { UserId = arabika.People[0].UserId }, arabika.People[0]);

        lk.Throws = true;
        var down = await Make(t, lk).ListAsync(Guid.NewGuid(), default);
        Assert.False(down.Single(r => r.Key == "arabika").Available);
        Assert.All(down.Where(r => r.Key != "arabika"), r => Assert.Empty(r.People));
    }

    [Fact]
    public async Task Turning_video_off_continues_after_one_participant_update_fails()
    {
        using var t = new TestDb();
        var organizer = t.AddUser("O1"); var booking = await BookNowAsync(t, organizer.Id, "Toraja");
        var lk = new FakeRoomAdmin(); lk.Fill("toraja", 2);
        var people = lk.Rooms["hall-toraja"];
        lk.ThrowFor = people[0].Identity;
        var svc = Make(t, lk);

        await svc.SetVideoAsync("toraja", false, "admin", default);

        var change = Assert.Single(lk.SourceChanges);
        Assert.Equal((FakeRoomAdmin.Media("toraja", booking), people[1].Identity), (change.Room, change.Identity));
        Assert.DoesNotContain("camera", change.Sources);
        Assert.False((await svc.VideoAsync(default))["toraja"]);
    }

    [Fact]
    public async Task Turning_video_off_strips_camera_from_everyone_in_the_room()
    {
        using var t = new TestDb();
        var organizer = t.AddUser("O1"); var booking = await BookNowAsync(t, organizer.Id, "Toraja");
        var lk = new FakeRoomAdmin(); lk.Fill("toraja", 2);
        await Make(t, lk).SetVideoAsync("toraja", false, "admin", default);
        Assert.Equal(2, lk.SourceChanges.Count);
        Assert.All(lk.SourceChanges, c => Assert.DoesNotContain("camera", c.Sources));
        Assert.False((await Make(t, lk).VideoAsync(default))["toraja"]);
    }

    // ---------------------------------------------------------------- final review fixes

    private static async Task<Guid> BookAsync(TestDb t, Guid organizer, string title, DateTime start, int minutes, string room, params Guid[] attendees)
    {
        var (_, id, _) = await IPop.Api.Endpoints.MeetingEndpoints.ApplyCreateAsync(t.Verify(), organizer,
            new(title, start, start.AddMinutes(minutes), null, room, null, attendees, null, null, null), default, nowUtc: start);
        return id!.Value;
    }

    [Fact]
    public async Task Room_cards_show_booking_titles_only_to_the_organizer_and_attendees()
    {
        using var t = new TestDb();
        var org = t.AddUser("ORG"); var guest = t.AddUser("GUEST"); var declined = t.AddUser("DECL"); var outsider = t.AddUser("OUT");
        var now = DateTime.UtcNow;
        await BookAsync(t, org.Id, "Salary review", now.AddMinutes(-10), 60, "SJA Hall · Arabika", guest.Id, declined.Id);
        await BookAsync(t, org.Id, "Layoff planning", now.AddHours(3), 30, "SJA Hall · Toraja", guest.Id, declined.Id);
        using (var db = t.Verify())
        {
            foreach (var a in db.MeetingAttendees.Where(a => a.UserId == declined.Id)) a.RsvpStatus = "decline";
            db.SaveChanges();
        }
        var svc = Make(t, new FakeRoomAdmin());

        foreach (var who in new[] { org.Id, guest.Id })
        {
            var rooms = await svc.ListAsync(who, default);
            Assert.Equal("Salary review", rooms.Single(r => r.Key == "arabika").Now!.Title);
            var next = rooms.Single(r => r.Key == "toraja").Next!;
            Assert.Equal(("Layoff planning", "User ORG"), (next.Title, next.OrganizerName));
        }
        foreach (var who in new[] { outsider.Id, declined.Id })
        {
            var rooms = await svc.ListAsync(who, default);
            var current = rooms.Single(r => r.Key == "arabika").Now!;
            Assert.Equal("Booked", current.Title);
            Assert.Null(current.OrganizerName);
            var next = rooms.Single(r => r.Key == "toraja").Next!;
            Assert.Equal("Booked", next.Title);
            Assert.Null(next.OrganizerName);
            Assert.Equal(now.AddHours(3), next.StartAt, TimeSpan.FromSeconds(1));   // times stay visible
        }
    }

    [Fact]
    public async Task List_asks_livekit_for_the_five_rooms_in_parallel_and_one_failing_room_marks_only_that_room()
    {
        using var t = new TestDb();
        var organizer = t.AddUser("O1");
        foreach (var (_, name) in IPop.Application.Hall.HallBooking.Rooms) await BookNowAsync(t, organizer.Id, name);
        var lk = new FakeRoomAdmin { ListDelay = TimeSpan.FromMilliseconds(150) };
        lk.Fill("arabika", 2);
        lk.ThrowRooms.Add("hall-kalosi");

        var rooms = await Make(t, lk).ListAsync(Guid.NewGuid(), default);

        Assert.True(lk.MaxConcurrentLists >= 5, $"at most {lk.MaxConcurrentLists} LiveKit calls ran together");
        Assert.False(rooms.Single(r => r.Key == "kalosi").Available);
        Assert.Empty(rooms.Single(r => r.Key == "kalosi").People);
        Assert.All(rooms.Where(r => r.Key != "kalosi"), r => Assert.True(r.Available));
        Assert.Equal(2, rooms.Single(r => r.Key == "arabika").People.Count);
    }

    [Fact]
    public async Task Room_people_names_come_from_the_directory_not_from_livekit()
    {
        using var t = new TestDb(); var budi = t.AddUser("BUDI");
        await BookNowAsync(t, budi.Id, "Robusta");
        var lk = new FakeRoomAdmin();
        lk.Rooms["hall-robusta"] = [
            new(budi.Id.ToString(), "The CEO", [], new Dictionary<string, string>()),          // spoofed display name
            new(Guid.NewGuid().ToString(), "Guest Name", [], new Dictionary<string, string>()), // not in Users: keeps LiveKit name
        ];
        var people = (await Make(t, lk).ListAsync(Guid.NewGuid(), default)).Single(r => r.Key == "robusta").People;
        Assert.Equal(["User BUDI", "Guest Name"], people.Select(p => p.Name));
        Assert.Equal(budi.Id, people[0].UserId);
    }

    [Fact]
    public async Task Join_carries_the_recording_retention_days()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        await BookNowAsync(t, me.Id, "Kalosi");
        await new DbSettings(t.Db).SetAsync(IPop.Application.Settings.SettingKeys.MeetingRoomsRetentionDays, 30, null);
        var r = await Make(t, new FakeRoomAdmin()).JoinAsync(me.Id, "kalosi", null, default);
        Assert.Equal(30, r.Join!.RetentionDays);
    }
}
