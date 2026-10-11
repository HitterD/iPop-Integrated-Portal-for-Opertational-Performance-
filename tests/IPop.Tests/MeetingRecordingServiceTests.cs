using IPop.Application.MeetingRooms;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class MeetingRecordingServiceTests
{
    internal sealed class FakeEgress : ILiveKitEgress
    {
        public bool IsConfigured { get; set; } = true;
        public List<(string Room, string Path)> Started { get; } = [];
        public List<string> Stopped { get; } = [];
        public Exception? StopThrows { get; set; }
        public Dictionary<string, LiveKitEgressInfo> Infos { get; } = new();
        public Task<string> StartRoomCompositeAsync(string room, string filepath, CancellationToken ct)
        { Started.Add((room, filepath)); return Task.FromResult($"EG_{Started.Count}"); }
        public Task StopAsync(string egressId, CancellationToken ct)
        {
            if (StopThrows is not null) throw StopThrows;
            Stopped.Add(egressId);
            return Task.CompletedTask;
        }
        public Task<LiveKitEgressInfo?> GetAsync(string egressId, CancellationToken ct) => Task.FromResult(Infos.GetValueOrDefault(egressId));
    }

    internal static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MeetingRooms:SpoolDir"] = Path.Combine(Path.GetTempPath(), "ipop-rec-tests"), ["MeetingRooms:EgressOutDir"] = "/out",
    }).Build();

    private static MeetingRecordingService Make(TestDb t, MeetingRoomServiceTests.FakeRoomAdmin lk, FakeEgress eg) =>
        new(t.Db, lk, eg, new DbSettings(t.Db), Config());

    [Fact]
    public async Task Start_records_the_room_and_makes_everyone_inside_a_viewer()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var b = t.AddUser("B1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>()), new(b.Id.ToString(), "B", [], new Dictionary<string, string>())];
        var eg = new FakeEgress();

        var r = await Make(t, lk, eg).StartAsync(a.Id, "kalosi", default);

        Assert.Null(r.Error);
        var (room, path) = eg.Started.Single();
        Assert.Equal("hall-kalosi", room);
        Assert.StartsWith("/out/kalosi/", path);
        Assert.EndsWith(".mp4", path);
        var row = await t.Verify().MeetingRecordings.Include(x => x.Viewers).SingleAsync();
        Assert.Equal((MeetingRecordingStatus.Recording, "Kalosi · ad-hoc"), (row.Status, row.Title));
        Assert.Equal(new[] { a.Id, b.Id }.Order(), row.Viewers.Select(v => v.UserId).Order());
    }

    [Fact]
    public async Task Title_is_the_booking_running_now()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        await IPop.Api.Endpoints.MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id,
            new("Production review", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(55), null, "SJA Hall · Arabika", null, null, null, null, null), default);
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-arabika"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        await Make(t, lk, new FakeEgress()).StartAsync(a.Id, "arabika", default);
        Assert.Equal("Production review", (await t.Verify().MeetingRecordings.SingleAsync()).Title);
    }

    [Fact]
    public async Task Only_one_server_recording_per_room_and_only_from_inside()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var out1 = t.AddUser("X1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        var eg = new FakeEgress(); var svc = Make(t, lk, eg);

        Assert.Equal("not_in_room", (await svc.StartAsync(out1.Id, "kalosi", default)).Error);
        Assert.Null((await svc.StartAsync(a.Id, "kalosi", default)).Error);
        Assert.Equal("already_recording", (await Make(t, lk, eg).StartAsync(a.Id, "kalosi", default)).Error);
        Assert.Single(eg.Started);
        Assert.Equal("egress_unavailable", (await Make(t, lk, new FakeEgress { IsConfigured = false }).StartAsync(a.Id, "toraja", default)).Error);
    }

    [Fact]
    public async Task Late_joiners_become_viewers_and_outsiders_cannot_view()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var late = t.AddUser("L1"); var never = t.AddUser("N1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        var svc = Make(t, lk, new FakeEgress());
        var rec = (await svc.StartAsync(a.Id, "kalosi", default)).Recording!;
        await svc.NoteJoinAsync(late.Id, "kalosi", default);
        await svc.NoteJoinAsync(late.Id, "kalosi", default);   // idempotent

        Assert.True(await svc.CanViewAsync(late.Id, false, rec.Id, default));
        Assert.False(await svc.CanViewAsync(never.Id, false, rec.Id, default));
        Assert.True(await svc.CanViewAsync(never.Id, true, rec.Id, default));
        Assert.Empty(await svc.ListAsync(never.Id, false, default));
    }

    [Fact]
    public async Task Listing_and_soft_delete_enforce_viewer_access_and_retention()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var outsider = t.AddUser("X1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        var svc = Make(t, lk, new FakeEgress());
        var rec = (await svc.StartAsync(a.Id, "kalosi", default)).Recording!;
        Assert.Empty(await svc.ListAsync(a.Id, false, default));
        Assert.Equal("not_in_room", (await svc.StopAsync(outsider.Id, rec.Id, default)).Error);
        Assert.Null((await svc.StopAsync(a.Id, rec.Id, default)).Error);
        var dto = Assert.Single(await svc.ListAsync(a.Id, false, default));
        Assert.Equal("processing", dto.Status);
        Assert.Equal(dto.StartedAt.AddDays(90), dto.DeleteAfter);
        Assert.Equal(1, dto.PeopleCount);
        Assert.Single(await svc.ListAsync(outsider.Id, true, default));
        Assert.Empty(await svc.ListAsync(outsider.Id, false, default));
        Assert.True(await svc.DeleteAsync(rec.Id, default));
        Assert.False(await svc.DeleteAsync(rec.Id, default));
        Assert.False(await svc.CanViewAsync(a.Id, false, rec.Id, default));
        Assert.False(await svc.CanViewAsync(outsider.Id, true, rec.Id, default));
        Assert.Empty(await svc.ListAsync(outsider.Id, true, default));
        Assert.Equal("not_found", (await svc.StopAsync(a.Id, rec.Id, default)).Error);
        Assert.Equal(MeetingRecordingStatus.Deleted, (await t.Verify().MeetingRecordings.SingleAsync()).Status);
    }

    [Fact]
    public async Task Unknown_rooms_and_unavailable_livekit_do_not_create_recordings()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        var svc = Make(t, lk, new FakeEgress());
        Assert.Equal("unknown_room", (await svc.StartAsync(a.Id, "missing", default)).Error);
        lk.Throws = true;
        Assert.Equal("egress_unavailable", (await svc.StartAsync(a.Id, "kalosi", default)).Error);
        lk.Throws = false; lk.IsConfigured = false;
        Assert.Equal("egress_unavailable", (await svc.StartAsync(a.Id, "kalosi", default)).Error);
        await svc.NoteJoinAsync(a.Id, "kalosi", default);
        Assert.Empty(await t.Verify().MeetingRecordings.ToListAsync());
        Assert.Empty(await t.Verify().MeetingRecordingViewers.ToListAsync());
    }

    [Theory]
    [InlineData("list")]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task Stop_outages_preserve_the_recording_reservation(string failure)
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        var eg = new FakeEgress(); var svc = Make(t, lk, eg);
        var rec = (await svc.StartAsync(a.Id, "kalosi", default)).Recording!;
        if (failure == "list") lk.Throws = true;
        else eg.StopThrows = failure == "timeout" ? new TaskCanceledException("timeout")
            : new HttpRequestException("down", null, System.Net.HttpStatusCode.ServiceUnavailable);

        Assert.Equal("egress_unavailable", (await svc.StopAsync(a.Id, rec.Id, default)).Error);
        var row = await t.Verify().MeetingRecordings.SingleAsync();
        Assert.Equal(MeetingRecordingStatus.Recording, row.Status);
        Assert.Null(row.EndedAt);
        lk.Throws = false;
        Assert.Equal("already_recording", (await svc.StartAsync(a.Id, "kalosi", default)).Error);
    }

    [Fact]
    public async Task Stop_404_moves_an_already_ended_recording_to_processing()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        var eg = new FakeEgress(); var svc = Make(t, lk, eg);
        var rec = (await svc.StartAsync(a.Id, "kalosi", default)).Recording!;
        eg.StopThrows = new HttpRequestException("already ended", null, System.Net.HttpStatusCode.NotFound);
        Assert.Null((await svc.StopAsync(a.Id, rec.Id, default)).Error);
        var row = await t.Verify().MeetingRecordings.SingleAsync();
        Assert.Equal(MeetingRecordingStatus.Processing, row.Status);
        Assert.NotNull(row.EndedAt);
    }

    [Fact]
    public async Task Delete_refuses_a_running_recording_and_keeps_its_reservation()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        var svc = Make(t, lk, new FakeEgress());
        var rec = (await svc.StartAsync(a.Id, "kalosi", default)).Recording!;
        Assert.False(await svc.DeleteAsync(rec.Id, default));
        Assert.Equal(MeetingRecordingStatus.Recording, (await t.Verify().MeetingRecordings.SingleAsync()).Status);
        Assert.Equal("already_recording", (await svc.StartAsync(a.Id, "kalosi", default)).Error);
    }

    [Fact]
    public async Task Stop_ends_the_egress_and_moves_to_processing()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        var eg = new FakeEgress(); var svc = Make(t, lk, eg);
        var rec = (await svc.StartAsync(a.Id, "kalosi", default)).Recording!;
        var r = await svc.StopAsync(a.Id, rec.Id, default);
        Assert.Null(r.Error);
        Assert.Equal(["EG_1"], eg.Stopped);
        Assert.Equal(MeetingRecordingStatus.Processing, (await t.Verify().MeetingRecordings.SingleAsync()).Status);
    }
}
