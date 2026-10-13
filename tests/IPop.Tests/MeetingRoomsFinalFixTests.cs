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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FakeEgress = IPop.Tests.MeetingRecordingServiceTests.FakeEgress;
using FakeRoomAdmin = IPop.Tests.MeetingRoomServiceTests.FakeRoomAdmin;
using FakeTokens = IPop.Tests.MeetingRoomServiceTests.FakeTokens;
using Nas = IPop.Tests.MeetingRecordingWorkerTests.Nas;

namespace IPop.Tests;

/// Final review fix wave (meeting rooms v2): booking boundaries for media/recording/chat/viewers (1), monotonic room
/// takeover ownership (2), legacy room-password backfill (3), environment boundaries (4), fail-closed overrun continuity (5).
public class MeetingRoomsFinalFixTests : IDisposable
{
    private readonly List<string> dirs = [];
    public void Dispose() { foreach (var d in dirs) if (Directory.Exists(d)) Directory.Delete(d, true); }

    /// "Watched continuously since forever": occupied rooms count as occupied without a break (no gap limit).
    private readonly RoomOccupancyTracker occ = new(DateTime.MinValue, TimeSpan.MaxValue);

    private static string Media(string key, Guid id) => FakeRoomAdmin.Media(key, id);

    private static IConfiguration Cfg(params (string Key, string? Value)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(x => x.Key, x => x.Value)).Build();

    private static async Task<Guid> Book(TestDb t, Guid organizer, DateTime start, int minutes, string room = "SJA Hall · Kalosi",
        params Guid[] attendees)
    {
        var (r, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Verify(), organizer,
            new("Weekly sync", start, start.AddMinutes(minutes), null, room, null, attendees, null, null, null), default, nowUtc: start);
        Assert.Equal(MeetingEndpoints.CreateResult.Created, r);
        return id!.Value;
    }

    private MeetingRoomService Rooms(TestDb t, FakeRoomAdmin lk, FakeEgress? eg = null, MeetingRoomMedia? media = null, IPopDbContext? db = null)
    {
        var d = db ?? t.Verify();
        return new(d, lk, new FakeTokens(), new DbSettings(d), new RoomPasswordLimiter(), new RoomMeetingCreator(d),
            occupancyTracker: occ, mediaRooms: media, egress: eg);
    }

    private MeetingRecordingService Recs(TestDb t, FakeRoomAdmin lk, FakeEgress eg, IConfiguration? cfg = null, Nas? nas = null,
        MeetingRoomMedia? media = null)
    {
        var d = t.Verify();
        return new(d, lk, eg, new DbSettings(d), cfg ?? MeetingRecordingServiceTests.Config(), occ, nas: nas, mediaRooms: media);
    }

    private ServiceProvider Sp(TestDb t, ILiveKitRoomAdmin lk, ILiveKitEgress? eg = null, IConfiguration? cfg = null, Nas? nas = null)
    {
        var sc = new ServiceCollection();
        sc.AddScoped<IPopDbContext>(_ => t.Verify());
        sc.AddScoped<IPop.Application.Settings.ISettings>(p => new DbSettings(p.GetRequiredService<IPopDbContext>()));
        sc.AddSingleton(lk);
        sc.AddSingleton(occ);
        if (eg is not null) sc.AddSingleton(eg);
        if (nas is not null) sc.AddSingleton<IPop.Application.Files.IFileAccessService>(nas);
        if (cfg is not null) { sc.AddSingleton(cfg); sc.AddSingleton(new MeetingRoomMedia(cfg)); }
        return sc.BuildServiceProvider();
    }

    private async Task<DateTime?> Tick(TestDb t, ILiveKitRoomAdmin lk, DateTime now, ILiveKitEgress? eg = null, IConfiguration? cfg = null)
    {
        using var sp = Sp(t, lk, eg, cfg);
        return await MeetingRoomBookingWorker.TickAsync(sp, now, default);
    }

    private static MeetingRecording AddRecording(TestDb t, Guid meetingId, Guid by, string egressId = "EG_A", string key = "kalosi",
        params Guid[] viewers)
    {
        var id = Guid.NewGuid();
        var r = new MeetingRecording
        {
            Id = id, RoomKey = key, Title = "A secret", EgressId = egressId, StartedById = by, MeetingId = meetingId,
            StartedAt = DateTime.UtcNow.AddMinutes(-20), Status = MeetingRecordingStatus.Recording, SpoolPath = $"{key}/x/{id:N}.mp4",
            Viewers = viewers.Select(v => new MeetingRecordingViewer { RecordingId = id, UserId = v }).ToList(),
        };
        using var db = t.Verify();
        db.MeetingRecordings.Add(r); db.SaveChanges();
        return r;
    }

    /// A ran until a moment ago (or still runs a few minutes); B's lock window opened 5 minutes ago, so B took over.
    private static async Task<(Guid A, Guid B, Domain.World W)> TakeoverAsync(TestDb t)
    {
        var w = new Domain.World(t.AddUser("AORG"), t.AddUser("BORG"), t.AddUser("BOTH"));
        var now = DateTime.UtcNow;
        var a = await Book(t, w.AOrg.Id, now.AddMinutes(-50), 55, attendees: w.Both.Id);
        var b = await Book(t, w.BOrg.Id, now.AddMinutes(5), 60, attendees: w.Both.Id);
        return (a, b, w);
    }

    private static class Domain { public sealed record World(User AOrg, User BOrg, User Both); }

    private static FakeEgress StopCompletes(Guid booking)
    {
        var eg = new FakeEgress();
        eg.Infos["EG_A"] = new("EGRESS_ACTIVE", null, null, null, null, Media("kalosi", booking));
        eg.OnStop = id => eg.Infos[id] = new("EGRESS_COMPLETE", null, null, null, null, Media("kalosi", booking));
        return eg;
    }

    // ================================================================ 1. booking boundary

    [Fact]
    public async Task Each_booking_gets_its_own_media_room_so_an_old_token_never_enters_the_next_booking()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var a = await Book(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60);
        var lk = new FakeRoomAdmin();
        var ja = (await Rooms(t, lk).JoinAsync(org.Id, "kalosi", null, default)).Join!;
        Assert.Equal(Media("kalosi", a), ja.LiveKitRoom);
        Assert.Contains(lk.Created, c => c.Room == ja.LiveKitRoom);

        await MeetingEndpoints.ApplyCancelAsync(t.Verify(), a, org.Id, default);
        var (b, err, _) = await Rooms(t, lk).UseNowAsync(org.Id, "kalosi", 30, null, [], default);
        Assert.Null(err);
        var jb = (await Rooms(t, lk).JoinAsync(org.Id, "kalosi", null, default)).Join!;
        Assert.Equal(Media("kalosi", b!.Value), jb.LiveKitRoom);
        Assert.NotEqual(ja.LiveKitRoom, jb.LiveKitRoom);
    }

    [Fact]
    public async Task Worker_stops_the_previous_bookings_recording_at_the_takeover()
    {
        using var t = new TestDb(); var (a, b, w) = await TakeoverAsync(t);
        var rec = AddRecording(t, a, w.AOrg.Id);
        var eg = StopCompletes(a); var lk = new FakeRoomAdmin();
        lk.Put("kalosi", w.BOrg.Id.ToString());
        await Tick(t, lk, DateTime.UtcNow, eg);
        Assert.Equal(["EG_A"], eg.Stopped);
        var row = await t.Verify().MeetingRecordings.SingleAsync();
        Assert.Equal(MeetingRecordingStatus.Processing, row.Status);
        Assert.NotNull(row.EndedAt);
    }

    [Theory]
    [InlineData("stop_throws")]
    [InlineData("still_active")]
    [InlineData("no_egress")]
    public async Task Next_booking_is_not_admitted_until_the_previous_recording_is_confirmed_stopped(string failure)
    {
        using var t = new TestDb(); var (a, _, w) = await TakeoverAsync(t);
        AddRecording(t, a, w.AOrg.Id);
        var eg = new FakeEgress();
        if (failure == "stop_throws") eg.StopThrows = new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);
        if (failure == "still_active") eg.Infos["EG_A"] = new("EGRESS_ACTIVE", null, null, null, null, Media("kalosi", a));   // stop "accepted", still recording
        var lk = new FakeRoomAdmin();
        var r = await Rooms(t, lk, failure == "no_egress" ? null : eg).JoinAsync(w.BOrg.Id, "kalosi", null, default);
        Assert.Equal("media_unavailable", r.Error);
        Assert.Null(r.Join);
        Assert.Empty(lk.Created);
        Assert.Equal(MeetingRecordingStatus.Recording, (await t.Verify().MeetingRecordings.SingleAsync()).Status);
    }

    [Fact]
    public async Task Next_booking_is_admitted_once_the_stop_is_confirmed_and_records_only_its_own_media_room()
    {
        using var t = new TestDb(); var (a, b, w) = await TakeoverAsync(t);
        AddRecording(t, a, w.AOrg.Id);
        var eg = StopCompletes(a); var lk = new FakeRoomAdmin();
        Assert.Null((await Rooms(t, lk, eg).JoinAsync(w.BOrg.Id, "kalosi", null, default)).Error);
        Assert.Equal(MeetingRecordingStatus.Processing, (await t.Verify().MeetingRecordings.SingleAsync()).Status);

        lk.Put("kalosi", w.BOrg.Id.ToString());
        var started = await Recs(t, lk, eg).StartAsync(w.BOrg.Id, "kalosi", default);
        Assert.Null(started.Error);
        Assert.Equal(Media("kalosi", b), eg.Started.Single().Room);
        Assert.Equal(b, (await t.Verify().MeetingRecordings.SingleAsync(x => x.Id == started.Recording!.Id)).MeetingId);
    }

    [Fact]
    public async Task Chat_of_the_current_booking_never_links_to_the_previous_bookings_recording()
    {
        using var t = new TestDb(); var (a, b, w) = await TakeoverAsync(t);
        AddRecording(t, a, w.AOrg.Id);
        var lk = new FakeRoomAdmin(); lk.Put("kalosi", w.BOrg.Id.ToString());
        var r = await new MeetingChatService(t.Verify(), lk, new MeetingChatRateLimiter(), occ).SendAsync(w.BOrg.Id, "kalosi", "B secret", default);
        Assert.Null(r.Error);
        var row = await t.Verify().MeetingChatMessages.SingleAsync();
        Assert.Equal(b, row.MeetingId);
        Assert.Null(row.RecordingId);
        Assert.Equal(Media("kalosi", b), lk.DataSent.Single().Room);
    }

    [Fact]
    public async Task Joining_the_next_booking_never_makes_anyone_a_viewer_of_the_previous_recording()
    {
        using var t = new TestDb(); var (a, _, w) = await TakeoverAsync(t);
        var rec = AddRecording(t, a, w.AOrg.Id, viewers: w.AOrg.Id);
        var lk = new FakeRoomAdmin();
        var svc = Recs(t, lk, new FakeEgress());
        await svc.NoteJoinAsync(w.BOrg.Id, "kalosi", default);
        await svc.NoteJoinAsync(w.Both.Id, "kalosi", default);   // invited to both bookings
        Assert.False(await svc.CanViewAsync(w.BOrg.Id, false, rec.Id, default));
        Assert.False(await svc.CanViewAsync(w.Both.Id, false, rec.Id, default));
    }

    [Fact]
    public async Task Recording_worker_samples_viewers_only_from_the_recordings_own_booking()
    {
        using var t = new TestDb(); var (a, b, w) = await TakeoverAsync(t);
        var rec = AddRecording(t, a, w.AOrg.Id, viewers: w.AOrg.Id);
        var lk = new FakeRoomAdmin();
        lk.PutIn(Media("kalosi", a));                                   // A's media room: empty now
        lk.Put("kalosi", w.Both.Id.ToString(), w.BOrg.Id.ToString());  // everybody else (B's meeting)
        var eg = StopCompletes(a); eg.Infos["EG_A"] = new("EGRESS_ACTIVE", null, null, null, null, Media("kalosi", a));
        // B is current: the recording worker stops A's recording instead of sampling B's people into it.
        using (var sp = WithGateway(t, lk, eg)) await MeetingRecordingWorker.FinishAsync(sp, default);
        Assert.Equal(["EG_A"], eg.Stopped);
        var viewers = await t.Verify().MeetingRecordingViewers.Where(v => v.RecordingId == rec.Id).Select(v => v.UserId).ToListAsync();
        Assert.Equal([w.AOrg.Id], viewers);
        Assert.DoesNotContain(lk.Listed, r => r == "hall-kalosi" || r == Media("kalosi", b));
    }

    private ServiceProvider WithGateway(TestDb t, ILiveKitRoomAdmin lk, ILiveKitEgress eg, IConfiguration? cfg = null, Nas? nas = null)
    {
        var sc = new ServiceCollection();
        sc.AddScoped<IPopDbContext>(_ => t.Verify());
        sc.AddScoped<IPop.Application.Settings.ISettings>(p => new DbSettings(p.GetRequiredService<IPopDbContext>()));
        sc.AddSingleton(lk); sc.AddSingleton(eg); sc.AddSingleton(occ);
        sc.AddSingleton<IPop.Application.Files.IFileAccessService>(nas ?? new Nas());
        sc.AddSingleton<IPop.Application.Notifications.INotificationGatewayService>(new NullGateway());
        sc.AddSingleton(cfg ?? Cfg(("MeetingRooms:SpoolDir", Path.Combine(Path.GetTempPath(), "ipop-ff-" + Guid.NewGuid().ToString("N")))));
        return sc.BuildServiceProvider();
    }

    private sealed class NullGateway : IPop.Application.Notifications.INotificationGatewayService
    {
        public Task<IPop.Application.Notifications.NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title,
            string message, IPop.Application.Notifications.NotificationTarget target, string priority = "normal", string? targetUrl = null,
            CancellationToken ct = default) =>
            Task.FromResult(new IPop.Application.Notifications.NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        public Task<IPop.Application.Notifications.NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotSupportedException();
        public Task<IPop.Application.Notifications.NotificationEnqueueResult> EnqueueAsync(Guid clientId, IPop.Application.Notifications.NotificationEnqueueRequest req, CancellationToken ct) => throw new NotSupportedException();
        public Task<IPop.Application.Notifications.NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IPop.Application.Notifications.NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IPop.Application.Notifications.NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Recording_chat_only_returns_lines_of_the_recordings_own_booking()
    {
        using var t = new TestDb(); var (a, b, w) = await TakeoverAsync(t);
        var rec = AddRecording(t, a, w.AOrg.Id, viewers: w.AOrg.Id);
        using (var db = t.Verify())
        {
            foreach (var (meeting, text) in new[] { (a, "A line"), (b, "B line linked by the old bug") })
                db.MeetingChatMessages.Add(new MeetingChatMessage
                {
                    Id = Guid.NewGuid(), MeetingId = meeting, RoomKey = "kalosi", UserId = w.AOrg.Id, Kind = "text", Text = text,
                    SentAt = DateTime.UtcNow, RecordingId = rec.Id, OffsetSeconds = 3,
                });
            db.SaveChanges();
        }
        var lines = await new MeetingChatService(t.Verify(), new FakeRoomAdmin(), new MeetingChatRateLimiter(), occ)
            .RecordingChatAsync(w.AOrg.Id, false, rec.Id, default);
        Assert.Equal(["A line"], lines!.Select(l => l.Text));
    }

    [Fact]
    public async Task Room_card_and_metadata_show_only_the_current_bookings_recording()
    {
        using var t = new TestDb(); var (a, b, w) = await TakeoverAsync(t);
        AddRecording(t, a, w.AOrg.Id);
        var lk = new FakeRoomAdmin(); lk.Put("kalosi", w.BOrg.Id.ToString());
        var card = (await Rooms(t, lk).ListAsync(w.BOrg.Id, default)).Single(r => r.Key == "kalosi");
        Assert.Equal(b, card.Now!.MeetingId);
        Assert.Null(card.Recording);
        var json = await RoomMetadata.ComposeAsync(t.Verify(), "kalosi", DateTime.UtcNow, [w.BOrg.Id.ToString()], RoomOccupancy.Continuous, default);
        Assert.DoesNotContain("recording", json);
        Assert.Contains(b.ToString(), json);
    }

    // ================================================================ 2. takeover ownership

    [Theory]
    [InlineData("move", false)]
    [InlineData("future", false)]
    [InlineData("cancel", false)]
    [InlineData("move", true)]
    [InlineData("future", true)]
    [InlineData("cancel", true)]
    public async Task Editing_the_takeover_booking_never_revives_its_predecessor(string edit, bool overrun)
    {
        using var t = new TestDb(); var aOrg = t.AddUser("A1"); var bOrg = t.AddUser("B1");
        var now = DateTime.UtcNow;
        // A still runs 5 more minutes, or ended 10 minutes ago and overruns an occupied room; B's window opened 5 minutes ago.
        var a = overrun ? await Book(t, aOrg.Id, now.AddMinutes(-40), 30) : await Book(t, aOrg.Id, now.AddMinutes(-50), 55);
        var bStart = now.AddMinutes(5);
        var b = await Book(t, bOrg.Id, bStart, 30);
        var at = DateTime.UtcNow;
        // No prior list/worker call: edit paths themselves must preserve a takeover whose window already opened.


        switch (edit)
        {
            case "move":
                Assert.Equal(MeetingEndpoints.UpdateResult.Applied, (await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), b, bOrg.Id,
                    new("Weekly sync", bStart, bStart.AddMinutes(30), "SJA Hall · Toraja", null, null, null, null), default)).Result);
                break;
            case "future":
                Assert.Equal(MeetingEndpoints.UpdateResult.Applied, (await MeetingEndpoints.ApplyUpdateAsync(t.Verify(), b, bOrg.Id,
                    new("Weekly sync", now.AddHours(3), now.AddHours(3.5), "SJA Hall · Kalosi", null, null, null, null), default)).Result);
                break;
            default:
                await MeetingEndpoints.ApplyCancelAsync(t.Verify(), b, bOrg.Id, default);
                break;
        }

        Assert.Null((await RoomAccess.BookingsAsync(t.Verify(), "kalosi", at, RoomOccupancy.Continuous, default)).Current);
        Assert.Null((await RoomAccess.BookingsAsync(t.Verify(), "kalosi", at.AddMinutes(1), RoomOccupancy.Continuous, default)).Current);
        var lk = new FakeRoomAdmin(); lk.PutIn(Media("kalosi", a), aOrg.Id.ToString());
        Assert.Equal("no_booking", (await Rooms(t, lk).ExtendAsync(aOrg.Id, "kalosi", default)).Error);
        Assert.Equal("no_booking", (await Rooms(t, lk).JoinAsync(aOrg.Id, "kalosi", null, default)).Error);
        // The worker moves A's people out of A's media room.
        await Tick(t, lk, DateTime.UtcNow);
        Assert.Contains((Media("kalosi", a), aOrg.Id.ToString()), lk.Removed);
    }

    [Fact]
    public async Task A_booking_made_after_the_takeover_booking_was_cancelled_is_not_blocked()
    {
        using var t = new TestDb(); var aOrg = t.AddUser("A1"); var bOrg = t.AddUser("B1"); var c = t.AddUser("C1");
        var now = DateTime.UtcNow;
        await Book(t, aOrg.Id, now.AddMinutes(-40), 30);
        var b = await Book(t, bOrg.Id, now.AddMinutes(5), 30);
        Assert.Equal(b, (await RoomAccess.BookingsAsync(t.Verify(), "kalosi", DateTime.UtcNow, RoomOccupancy.Continuous, default)).Current?.MeetingId);
        await MeetingEndpoints.ApplyCancelAsync(t.Verify(), b, bOrg.Id, default);
        var lk = new FakeRoomAdmin();
        var (d, err, _) = await Rooms(t, lk).UseNowAsync(c.Id, "kalosi", 30, null, [], default);
        Assert.Null(err);
        Assert.Equal(d, (await RoomAccess.BookingsAsync(t.Verify(), "kalosi", DateTime.UtcNow, RoomOccupancy.Empty, default)).Current?.MeetingId);
        Assert.Null((await Rooms(t, lk).JoinAsync(c.Id, "kalosi", null, default)).Error);
    }

    // ================================================================ 3. legacy password backfill

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> f) => Lines.Add(f(state, e));
    }

    [Fact]
    public async Task Legacy_hall_bookings_without_a_password_get_a_secure_six_digit_one_once()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var guest = t.AddUser("G1");
        var now = DateTime.UtcNow;
        Meeting M(string loc, DateTime start, string? pw, string status = MeetingStatus.Active) => new()
        {
            Id = Guid.NewGuid(), Title = "Legacy", StartAt = start, EndAt = start.AddHours(1), Location = loc, OrganizerId = org.Id,
            Status = status, RoomPassword = pw, CreatedAt = now.AddDays(-3),
        };
        var running = M("SJA Hall · Kalosi", now.AddMinutes(-5), null);
        var future = M("sja hall - toraja", now.AddDays(2), "  ");
        var cancelled = M("SJA Hall · Robusta", now.AddDays(1), null, MeetingStatus.Cancelled);
        var other = M("Lantai 3", now.AddDays(1), null);
        var past = M("SJA Hall · Arabika", now.AddDays(-3), null);
        var kept = M("SJA Hall · Arabika", now.AddDays(1), "123456");
        using (var db = t.Verify()) { db.Meetings.AddRange(running, future, cancelled, other, past, kept); db.SaveChanges(); }

        var log = new CapturingLogger();
        Assert.Equal(2, await RoomPasswordBackfill.RunAsync(t.Verify(), now, default, log));
        var after = t.Verify().Meetings.AsNoTracking().ToDictionary(m => m.Id, m => m.RoomPassword);
        Assert.Matches("^[0-9]{6}$", after[running.Id]);
        Assert.Matches("^[0-9]{6}$", after[future.Id]);
        Assert.Null(after[cancelled.Id]);
        Assert.Null(after[other.Id]);
        Assert.Null(after[past.Id]);
        Assert.Equal("123456", after[kept.Id]);
        Assert.All(log.Lines, l => Assert.DoesNotContain(after[running.Id]!, l));

        Assert.Equal(0, await RoomPasswordBackfill.RunAsync(t.Verify(), now, default, log));   // idempotent
        Assert.Equal(after[running.Id], t.Verify().Meetings.Single(m => m.Id == running.Id).RoomPassword);

        // The organizer can share it; a guest gets in with it.
        Assert.Null((await Rooms(t, new FakeRoomAdmin()).JoinAsync(guest.Id, "kalosi", after[running.Id], default)).Error);
    }

    // ================================================================ 4. environment boundaries

    [Fact]
    public void Media_namespace_is_configurable_and_never_matches_another_environment()
    {
        var id = Guid.NewGuid();
        var prod = new MeetingRoomMedia(Cfg());
        var qa = new MeetingRoomMedia(Cfg(("MeetingRooms:MediaNamespace", "qa-hall")));
        Assert.Equal($"hall-kalosi-{id:N}", prod.Room("kalosi", id));
        Assert.Equal($"qa-hall-kalosi-{id:N}", qa.Room("kalosi", id));
        Assert.Equal(("kalosi", id), qa.Parse(qa.Room("kalosi", id)));
        Assert.Null(qa.Parse(prod.Room("kalosi", id)));
        Assert.Null(prod.Parse(qa.Room("kalosi", id)));
        Assert.Null(prod.Parse("hall-kalosi"));                       // v1 / sja-hall physical room
        Assert.Null(prod.Parse("call-" + id.ToString("N")));
        Assert.Throws<ArgumentException>(() => prod.Room("gayo", id));
        Assert.Throws<InvalidOperationException>(() => new MeetingRoomMedia(Cfg(("MeetingRooms:MediaNamespace", "QA hall/../x"))));
        Assert.True(prod.BookingWorkerEnabled);
        Assert.False(new MeetingRoomMedia(Cfg(("MeetingRooms:BookingWorkerEnabled", "false"))).BookingWorkerEnabled);
    }

    [Fact]
    public async Task A_qa_api_never_lists_removes_rewrites_or_stops_production_media()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var prodUser = Guid.NewGuid().ToString();
        var b = await Book(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60);
        // A production recording row copied into the QA database, its Egress recording the production media room.
        AddRecording(t, b, org.Id, "EG_PROD");
        var cfg = Cfg(("MeetingRooms:MediaNamespace", "qa-hall"));
        var qa = new MeetingRoomMedia(cfg);
        var lk = new FakeRoomAdmin();
        lk.PutIn($"hall-kalosi-{b:N}", prodUser);   // production room of the same (copied) booking
        lk.PutIn("hall-kalosi", prodUser);          // v1 / sja-hall room
        lk.PutIn(qa.Room("kalosi", b), org.Id.ToString());
        var eg = new FakeEgress(); eg.Infos["EG_PROD"] = new("EGRESS_ACTIVE", null, null, null, null, $"hall-kalosi-{b:N}");
        // ... and a copied production row of an OLDER booking that the QA worker would otherwise "finalize".
        var old = Guid.NewGuid();
        using (var db = t.Verify())
        {
            db.MeetingRecordings.Add(new MeetingRecording { Id = Guid.NewGuid(), RoomKey = "kalosi", Title = "prod", EgressId = "EG_PROD_OLD",
                StartedById = org.Id, MeetingId = old, StartedAt = DateTime.UtcNow.AddHours(-1), Status = MeetingRecordingStatus.Processing });
            db.SaveChanges();
        }
        eg.Infos["EG_PROD_OLD"] = new("EGRESS_ACTIVE", null, null, null, null, $"hall-kalosi-{old:N}");

        await Tick(t, lk, DateTime.UtcNow, eg, cfg);
        using (var recordingSp = WithGateway(t, lk, eg, cfg))
            await MeetingRecordingWorker.FinishAsync(recordingSp, default);
        Assert.Equal(MeetingRecordingStatus.Recording, (await t.Verify().MeetingRecordings.SingleAsync(r => r.EgressId == "EG_PROD")).Status);
        var join = await Rooms(t, lk, eg, qa).JoinAsync(org.Id, "kalosi", null, default);

        Assert.Null(join.Error);
        Assert.Equal(qa.Room("kalosi", b), join.Join!.LiveKitRoom);
        Assert.All(lk.Listed, r => Assert.StartsWith("qa-hall-", r));
        Assert.All(lk.Removed, r => Assert.StartsWith("qa-hall-", r.Room));
        Assert.All(lk.MetadataUpdates, r => Assert.StartsWith("qa-hall-", r.Room));
        Assert.All(lk.Created, r => Assert.StartsWith("qa-hall-", r.Room));
        Assert.Empty(eg.Stopped);
    }

    [Fact]
    public async Task Disabled_booking_worker_does_nothing()
    {
        using var t = new TestDb(); var x = t.AddUser("X1");
        var stale = Media("kalosi", Guid.NewGuid());   // e.g. an old booking's token reused
        var lk = new FakeRoomAdmin(); lk.PutIn(stale, x.Id.ToString(), "EG_rec");
        Assert.Null(await Tick(t, lk, DateTime.UtcNow, cfg: Cfg(("MeetingRooms:BookingWorkerEnabled", "false"))));
        Assert.Empty(lk.Listed);
        Assert.Empty(lk.Removed);
        await Tick(t, lk, DateTime.UtcNow);   // enabled (default): the stale media room is emptied, the recorder left alone
        Assert.Equal([(stale, x.Id.ToString())], lk.Removed);
    }

    [Theory]
    [InlineData("/sja/SJA SPJ/ICT/recordings-qa/kalosi/2026-10-12/a.mp4", "/sja/SJA SPJ/ICT/recordings-qa/kalosi/2026-10-12/a.mp4")]
    [InlineData("/sja/SJA SPJ/ICT/recordings-qa/a.mp4", "/sja/SJA SPJ/ICT/recordings-qa/a.mp4")]
    [InlineData("/sja/SJA SPJ/ICT/recordings/kalosi/a.mp4", null)]             // production copy
    [InlineData("/sja/SJA SPJ/ICT/recordings-qa", null)]                       // the root itself
    [InlineData("/sja/SJA SPJ/ICT/recordings-qa/", null)]
    [InlineData("/sja/SJA SPJ/ICT/recordings-qa-old/a.mp4", null)]             // prefix look-alike
    [InlineData("/sja/SJA SPJ/ICT/recordings-qa/../recordings/a.mp4", null)]   // traversal
    [InlineData("/sja/SJA SPJ/ICT/recordings-qa/./a.mp4", null)]
    [InlineData("/sja/SJA SPJ/ICT/recordings-qa//a.mp4", null)]
    [InlineData("sja/SJA SPJ/ICT/recordings-qa/a.mp4", null)]                  // relative
    [InlineData("/sja/SJA SPJ/ICT/recordings-qa\\..\\a.mp4", null)]
    [InlineData("/sja/SJA SPJ/ICT/recordings-qa/kalosi/", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Nas_deletes_are_only_for_files_strictly_under_the_configured_folder(string? path, string? expected)
    {
        Assert.Equal(expected, RecordingPaths.NasFile("/sja/SJA SPJ/ICT/recordings-qa", path));
        Assert.Equal(expected, RecordingPaths.NasFile("/sja/SJA SPJ/ICT/recordings-qa/", path));
        Assert.Null(RecordingPaths.NasFile("/", path));
        Assert.Null(RecordingPaths.NasFile(null, path));
        Assert.Null(RecordingPaths.NasFile("relative/root", path));
    }

    [Theory]
    [InlineData("kalosi/2026-10-12/a.mp4", true)]
    [InlineData("../outside.mp4", false)]
    [InlineData("kalosi/../../outside.mp4", false)]
    [InlineData("C:/outside.mp4", false)]
    [InlineData("/outside.mp4", false)]
    [InlineData("\\\\server\\share\\a.mp4", false)]
    [InlineData("", false)]
    public void Spool_paths_stay_inside_the_spool_folder(string relative, bool ok)
    {
        var root = Path.Combine(Path.GetTempPath(), "ipop-spool-root");
        var local = RecordingPaths.LocalFile(root, relative);
        if (!ok) { Assert.Null(local); return; }
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "kalosi", "2026-10-12", "a.mp4")), local);
    }

    private (TestDb T, User Org, MeetingRecording Rec) ReadyRecording(string nasPath, DateTime? startedAt = null, string? spool = null)
    {
        var t = new TestDb(); var org = t.AddUser("O1");
        var rec = AddRecording(t, Guid.NewGuid(), org.Id, viewers: org.Id);
        using var db = t.Verify();
        var row = db.MeetingRecordings.Single();
        row.Status = MeetingRecordingStatus.Ready; row.NasPath = nasPath; row.EndedAt = DateTime.UtcNow;
        if (startedAt is { } s) row.StartedAt = s;
        if (spool is not null) row.SpoolPath = spool;
        db.SaveChanges();
        return (t, org, row);
    }

    private static IConfiguration QaCfg(string spool) => Cfg(("MeetingRooms:NasFolder", "/sja/SJA SPJ/ICT/recordings-qa"),
        ("MeetingRooms:SpoolDir", spool), ("MeetingRooms:CacheDir", Path.Combine(spool, "..", "cache")));

    [Fact]
    public async Task Deleting_a_copied_production_recording_never_touches_its_nas_file()
    {
        var spool = Path.Combine(Path.GetTempPath(), "ipop-ff-" + Guid.NewGuid().ToString("N"), "spool"); dirs.Add(Path.GetDirectoryName(spool)!);
        var outside = Path.Combine(Path.GetDirectoryName(spool)!, "outside.mp4");
        Directory.CreateDirectory(spool); File.WriteAllBytes(outside, [1]);
        var (t, org, rec) = ReadyRecording("/sja/SJA SPJ/ICT/recordings/kalosi/2026-10-01/prod.mp4", spool: "../outside.mp4");
        using var _ = t;
        var nas = new Nas();
        Assert.Null(await Recs(t, new FakeRoomAdmin(), new FakeEgress(), QaCfg(spool), nas).DeleteAsync(org.Id, true, rec.Id, default));
        Assert.Empty(nas.Deleted);
        var row = await t.Verify().MeetingRecordings.SingleAsync();
        Assert.Equal(MeetingRecordingStatus.Deleted, row.Status);
        Assert.Equal("/sja/SJA SPJ/ICT/recordings/kalosi/2026-10-01/prod.mp4", row.NasPath);   // not cleared as if deleted
        Assert.True(File.Exists(outside));                                                       // spool guard
    }

    [Fact]
    public async Task Deleting_a_recording_under_the_configured_folder_removes_its_file()
    {
        var spool = Path.Combine(Path.GetTempPath(), "ipop-ff-" + Guid.NewGuid().ToString("N"), "spool"); dirs.Add(Path.GetDirectoryName(spool)!);
        var (t, org, rec) = ReadyRecording("/sja/SJA SPJ/ICT/recordings-qa/kalosi/2026-10-12/qa.mp4");
        using var _ = t;
        var nas = new Nas();
        Assert.Null(await Recs(t, new FakeRoomAdmin(), new FakeEgress(), QaCfg(spool), nas).DeleteAsync(org.Id, true, rec.Id, default));
        Assert.Equal(["/sja/SJA SPJ/ICT/recordings-qa/kalosi/2026-10-12/qa.mp4"], nas.Deleted);
        Assert.Null((await t.Verify().MeetingRecordings.SingleAsync()).NasPath);
    }

    [Fact]
    public async Task Purge_never_deletes_a_copied_production_file_and_keeps_its_path()
    {
        var spool = Path.Combine(Path.GetTempPath(), "ipop-ff-" + Guid.NewGuid().ToString("N"), "spool"); dirs.Add(Path.GetDirectoryName(spool)!);
        var (t, _, rec) = ReadyRecording("/sja/SJA SPJ/ICT/recordings/kalosi/2026-01-01/prod.mp4", DateTime.UtcNow.AddDays(-200));
        using var __ = t;
        var nas = new Nas();
        using var sp = Sp(t, new FakeRoomAdmin(), cfg: QaCfg(spool), nas: nas);
        await MeetingRecordingWorker.PurgeAsync(sp, DateTime.UtcNow, default);
        Assert.Empty(nas.Deleted);
        Assert.Equal("/sja/SJA SPJ/ICT/recordings/kalosi/2026-01-01/prod.mp4", (await t.Verify().MeetingRecordings.SingleAsync()).NasPath);
    }

    [Fact]
    public async Task Unknown_egress_room_is_not_a_confirmed_stop()
    {
        using var t = new TestDb(); var (a, _, w) = await TakeoverAsync(t);
        AddRecording(t, a, w.AOrg.Id);
        var eg = new FakeEgress(); eg.Infos["EG_A"] = new("EGRESS_COMPLETE", null, null, null, null);
        var result = await Rooms(t, new FakeRoomAdmin(), eg).JoinAsync(w.BOrg.Id, "kalosi", null, default);
        Assert.Equal("media_unavailable", result.Error);
        Assert.Empty(eg.Stopped);
    }

    [Fact]
    public async Task Ownership_high_water_survives_a_new_context_and_out_of_order_read()
    {
        using var t = new TestDb(); var (a, b, _) = await TakeoverAsync(t);
        var now = DateTime.UtcNow;
        Assert.Equal(b, (await RoomAccess.BookingsAsync(t.Verify(), "kalosi", now, RoomOccupancy.Continuous, default)).Current?.MeetingId);
        // An old request that started before B's window cannot overwrite persistent B ownership with A.
        Assert.Null((await RoomAccess.BookingsAsync(t.Verify(), "kalosi", now.AddMinutes(-10), RoomOccupancy.Continuous, default)).Current);
        Assert.Equal(b, (await RoomAccess.BookingsAsync(t.Verify(), "kalosi", now.AddSeconds(1), RoomOccupancy.Continuous, default)).Current?.MeetingId);
        Assert.DoesNotContain(t.Verify().MeetingRoomOwnerships, x => x.MeetingId == a);
    }

    // Scoped review round 1 regressions
    private static IConfiguration Deployment(string? mode = null, string? ns = "qa-hall", string? nas = "/qa/recordings",
        string? spool = "C:/ipop-qa/spool", string? cache = "C:/ipop-qa/cache") => Cfg(
        ("MeetingRooms:DeploymentMode", mode), ("ASPNETCORE_ENVIRONMENT", "Development"),
        ("MeetingRooms:MediaNamespace", ns), ("MeetingRooms:BookingWorkerEnabled", "false"),
        ("MeetingRooms:NasFolder", nas), ("MeetingRooms:SpoolDir", spool), ("MeetingRooms:CacheDir", cache));

    [Fact]
    public void Explicit_production_deployment_is_accepted_even_in_Development()
    {
        MeetingRoomMedia.ValidateDeployment(Deployment("Production", "hall", "/sja/SJA SPJ/ICT/recordings",
            "F:/ipop-data/recordings-spool", "F:/ipop-data/recordings-cache"));
        MeetingRoomMedia.ValidateDeployment(Deployment()); // default Isolated with safe explicit roots
    }

    [Theory]
    [InlineData(null, "qa-hall", "/qa/recordings", null, "C:/ipop-qa/cache")]
    [InlineData(null, "qa-hall", "/qa/recordings", "C:/ipop-qa/spool", null)]
    [InlineData(null, "hall", "/qa/recordings", "C:/ipop-qa/spool", "C:/ipop-qa/cache")]
    [InlineData("Isolated", "qa-hall", "/sja/SJA SPJ/ICT/recordings", "C:/ipop-qa/spool", "C:/ipop-qa/cache")]
    [InlineData("Isolated", "qa-hall", "/sja/SJA SPJ/ICT", "C:/ipop-qa/spool", "C:/ipop-qa/cache")]
    [InlineData("Isolated", "qa-hall", "/qa/recordings", "F:/ipop-data", "C:/ipop-qa/cache")]
    [InlineData("Isolated", "qa-hall", "/qa/recordings", "C:/ipop-qa/spool", "C:/ipop-qa/spool/cache")]
    [InlineData("Isolated", "qa-hall", "/qa/recordings", "F:/ipop-data/recordings-spool", "C:/ipop-qa/cache")]
    [InlineData("Isolated", "qa-hall", "/qa/recordings", "C:/ipop-qa/spool", "F:/ipop-data/recordings-cache")]
    [InlineData("Isolated", "qa-hall", "/qa/recordings", "C:/ipop-qa/spool", "c:/ipop-qa/spool/")]
    [InlineData("garbage", "qa-hall", "/qa/recordings", "C:/ipop-qa/spool", "C:/ipop-qa/cache")]
    public void Isolated_deployment_rejects_missing_production_or_equal_roots(string? mode, string ns, string nas, string? spool, string? cache) =>
        Assert.Throws<InvalidOperationException>(() => MeetingRoomMedia.ValidateDeployment(Deployment(mode, ns, nas, spool, cache)));

    [Theory]
    [InlineData("foreign", false)]
    [InlineData("nested", true)]
    public async Task Playback_never_downloads_a_foreign_NAS_row(string kind, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "ipop-review-" + Guid.NewGuid().ToString("N")); dirs.Add(root);
        var nas = new Nas();
        var cfg = QaCfg(Path.Combine(root, "spool"));
        var row = new MeetingRecording { Id = Guid.NewGuid(), RoomKey = "kalosi", Title = "Test", EgressId = "EG_TEST", NasPath = kind == "foreign"
            ? "/sja/SJA SPJ/ICT/recordings/prod.mp4" : "/sja/SJA SPJ/ICT/recordings-qa/kalosi/day/a.mp4" };
        var result = await new RecordingFileCache(nas, cfg).OpenAsync(row, default);
        Assert.Equal(allowed, result is not null);
        Assert.Equal(allowed ? 1 : 0, nas.Downloads);
    }

    [Fact]
    public async Task Foreign_recording_is_quarantined_from_lists_and_tickets()
    {
        var root = Path.Combine(Path.GetTempPath(), "ipop-review-" + Guid.NewGuid().ToString("N")); dirs.Add(root);
        var (t, org, rec) = ReadyRecording("/sja/SJA SPJ/ICT/recordings/prod.mp4"); using var _ = t;
        var svc = Recs(t, new FakeRoomAdmin(), new FakeEgress(), QaCfg(Path.Combine(root, "spool")));
        Assert.Empty(await svc.ListAsync(org.Id, false, default));
        Assert.Empty(await svc.ListAllAsync(default));
        Assert.False(await svc.CanViewAsync(org.Id, false, rec.Id, default));
        Assert.False(await svc.CanViewAsync(org.Id, true, rec.Id, default));
    }

    [Fact]
    public async Task Interrupted_start_after_takeover_retries_until_room_egress_cessation_is_confirmed()
    {
        using var t = new TestDb(); var (a, _, w) = await TakeoverAsync(t);
        var rec = AddRecording(t, a, w.AOrg.Id, "");
        var eg = new FakeEgress { ListThrows = new TaskCanceledException("timeout") };
        var lk = new FakeRoomAdmin();
        using (var sp = WithGateway(t, lk, eg)) await MeetingRecordingWorker.FinishAsync(sp, default);
        Assert.Equal(MeetingRecordingStatus.Recording, t.Verify().MeetingRecordings.Single().Status);
        Assert.Equal("media_unavailable", (await Rooms(t, lk, eg).JoinAsync(w.BOrg.Id, "kalosi", null, default)).Error);
        eg.ListThrows = null;
        eg.Infos["EG_LOST"] = new("EGRESS_ACTIVE", null, null, null, null, Media("kalosi", a));
        using (var sp = WithGateway(t, lk, eg)) await MeetingRecordingWorker.FinishAsync(sp, default);
        Assert.Equal(MeetingRecordingStatus.Recording, t.Verify().MeetingRecordings.Single().Status);
        Assert.Equal("media_unavailable", (await Rooms(t, lk, eg).JoinAsync(w.BOrg.Id, "kalosi", null, default)).Error);
        eg.OnStop = id => eg.Infos[id] = eg.Infos[id] with { Status = "EGRESS_COMPLETE" };
        using (var sp = WithGateway(t, lk, eg)) await MeetingRecordingWorker.FinishAsync(sp, default);
        Assert.Equal(MeetingRecordingStatus.Failed, t.Verify().MeetingRecordings.Single().Status);
        Assert.Null((await Rooms(t, lk, eg).JoinAsync(w.BOrg.Id, "kalosi", null, default)).Error);
    }

    private sealed class SequenceClock(DateTime before, DateTime after) : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Increment(ref calls) == 1 ? before : after);
    }

    [Fact]
    public async Task Chat_sampled_in_A_cannot_post_into_B_even_if_sender_is_invited_to_both()
    {
        using var t = new TestDb();
        var w = new Domain.World(t.AddUser("AORG"), t.AddUser("BORG"), t.AddUser("BOTH"));
        var before = DateTime.UtcNow;
        var a = await Book(t, w.AOrg.Id, before.AddMinutes(-50), 55, attendees: w.Both.Id);
        var b = await Book(t, w.BOrg.Id, before.AddMinutes(10).AddSeconds(5), 60, attendees: w.Both.Id);
        var after = before.AddSeconds(6);
        var lk = new FakeRoomAdmin(); lk.PutIn(Media("kalosi", a), w.Both.Id.ToString()); lk.PutIn(Media("kalosi", b));
        AddRecording(t, b, w.BOrg.Id);
        var svc = new MeetingChatService(t.Verify(), lk, new MeetingChatRateLimiter(), occ, timeProvider: new SequenceClock(before, after));
        var result = await svc.SendAsync(w.Both.Id, "kalosi", "must not enter B", default);
        Assert.NotNull(result.Error);
        Assert.Empty(t.Verify().MeetingChatMessages);
        Assert.Empty(lk.DataSent);
    }

    // ================================================================ 5. overrun continuity (fail closed)

    [Fact]
    public void Overrun_needs_continuous_observations_without_a_gap_over_45_seconds()
    {
        var end = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var booking = new Booking(Guid.NewGuid(), "x", end.AddHours(-1), end, Guid.NewGuid(), null);
        var tr = new RoomOccupancyTracker(end.AddHours(-2));
        tr.Observe("r", true, end.AddSeconds(-20));
        Assert.True(RoomAccess.Overruns(booking, end.AddSeconds(20), tr.Observe("r", true, end.AddSeconds(20))));
        Assert.True(RoomAccess.Overruns(booking, end.AddSeconds(60), tr.Observe("r", true, end.AddSeconds(60))));
        // A missed tick: 70 s without an observation → the overrun is over for good.
        Assert.False(RoomAccess.Overruns(booking, end.AddSeconds(130), tr.Observe("r", true, end.AddSeconds(130))));
        Assert.False(RoomAccess.Overruns(booking, end.AddSeconds(150), tr.Observe("r", true, end.AddSeconds(150))));

        // A failed listing breaks the chain too.
        var tr2 = new RoomOccupancyTracker(end.AddHours(-2));
        tr2.Observe("r", true, end.AddSeconds(-20));
        tr2.ObserveFailed("r");
        Assert.False(RoomAccess.Overruns(booking, end.AddSeconds(10), tr2.Observe("r", true, end.AddSeconds(10))));

        // First seen only after the end (e.g. after an API restart): no overrun.
        var tr3 = new RoomOccupancyTracker(end.AddHours(-2));
        Assert.False(RoomAccess.Overruns(booking, end.AddSeconds(5), tr3.Observe("r", true, end.AddSeconds(5))));
    }

    [Fact]
    public async Task Recording_worker_never_adds_an_outsider_in_the_correct_media_session()
    {
        using var t = new TestDb(); var org = t.AddUser("O1"); var outsider = t.AddUser("X1");
        var b = await Book(t, org.Id, DateTime.UtcNow.AddMinutes(-5), 60);
        var rec = AddRecording(t, b, org.Id, viewers: org.Id);
        var lk = new FakeRoomAdmin(); lk.PutIn(Media("kalosi", b), org.Id.ToString(), outsider.Id.ToString());
        var eg = new FakeEgress(); eg.Infos["EG_A"] = new("EGRESS_ACTIVE", null, null, null, null, Media("kalosi", b));
        using var sp = WithGateway(t, lk, eg);
        await MeetingRecordingWorker.FinishAsync(sp, default);
        Assert.False(await t.Verify().MeetingRecordingViewers.AnyAsync(v => v.RecordingId == rec.Id && v.UserId == outsider.Id));
    }

    [Fact]
    public async Task Worker_ends_an_overrun_after_a_missed_tick()
    {
        using var t = new TestDb(); var org = t.AddUser("O1");
        var start = RoomAccess.DayStartUtc(DateTime.UtcNow).AddHours(9);
        var id = await Book(t, org.Id, start, 60);
        var end = start.AddHours(1);
        var tracker = new RoomOccupancyTracker(start.AddHours(-1));
        var lk = new FakeRoomAdmin(); lk.PutIn(Media("kalosi", id), org.Id.ToString());
        var sc = new ServiceCollection();
        sc.AddScoped<IPopDbContext>(_ => t.Verify()); sc.AddSingleton<ILiveKitRoomAdmin>(lk); sc.AddSingleton(tracker);
        using var sp = sc.BuildServiceProvider();
        foreach (var s in new[] { -20, 10, 40 }) await MeetingRoomBookingWorker.TickAsync(sp, end.AddSeconds(s), default);
        Assert.Empty(lk.Removed);
        await MeetingRoomBookingWorker.TickAsync(sp, end.AddSeconds(100), default);   // 60 s gap
        Assert.Equal([(Media("kalosi", id), org.Id.ToString())], lk.Removed);
    }
}
