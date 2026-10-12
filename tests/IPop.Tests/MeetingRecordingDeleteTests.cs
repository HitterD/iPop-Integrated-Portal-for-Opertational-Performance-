using System.Security.Claims;
using IPop.Api.Endpoints;
using IPop.Core.Entities;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Rbac;
using IPop.Infrastructure.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Meeting rooms v2 §2 items 7–8: recordings are listed only for people who were in the room (Admins audit from
/// Administration), and the starter, the booking organizer or an Admin deletes them for everyone, NAS file included.
public class MeetingRecordingDeleteTests : IDisposable
{
    private const string Secret = "test-secret-0123456789-0123456789-abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ipop-recdel-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private IConfiguration Cfg() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MeetingRooms:SpoolDir"] = Path.Combine(_dir, "spool"), ["MeetingRooms:CacheDir"] = Path.Combine(_dir, "cache"),
        ["MeetingRooms:EgressOutDir"] = "/out",
    }).Build();

    private sealed record World(TestDb T, User Organizer, User Starter, User Viewer, User Outsider, User Admin, Guid MeetingId, Guid RecordingId,
        MeetingRecordingWorkerTests.Nas Nas, MeetingRoomServiceTests.FakeRoomAdmin Lk);

    private MeetingRecordingService Svc(World w, IPop.Infrastructure.Persistence.IPopDbContext? db = null)
    {
        var d = db ?? w.T.Db; var cfg = Cfg();
        return new MeetingRecordingService(d, w.Lk, new MeetingRecordingServiceTests.FakeEgress(), new DbSettings(d), cfg,
            nas: w.Nas, fileCache: new RecordingFileCache(w.Nas, cfg));
    }

    /// The organizer books Kalosi; the starter, another viewer and the (non-organizer) starter record it; it reaches the NAS.
    private async Task<World> SetupAsync()
    {
        var t = new TestDb();
        var role = new Role { Id = Guid.NewGuid(), Name = "Employee" };
        var perm = new Permission { Id = Guid.NewGuid(), Code = "calls.use" };
        t.Db.Roles.Add(role); t.Db.Permissions.Add(perm);
        t.Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
        var organizer = t.AddUser("ORG", mustChange: false); var starter = t.AddUser("START", mustChange: false);
        var viewer = t.AddUser("VIEW", mustChange: false); var outsider = t.AddUser("OUT", mustChange: false);
        var admin = t.AddAdmin("ADM");
        foreach (var u in new[] { organizer, starter, viewer, outsider, admin }) t.Db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = role.Id });
        t.Db.SaveChanges();
        await MeetingEndpoints.ApplyCreateAsync(t.Db, organizer.Id,
            new("Weekly sync", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(55), null, "SJA Hall · Kalosi", null, null, null, null, null), default);
        var meetingId = await t.Db.Meetings.Select(m => m.Id).SingleAsync();
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(starter.Id.ToString(), "S", [], new Dictionary<string, string>()),
            new(viewer.Id.ToString(), "V", [], new Dictionary<string, string>())];
        var w = new World(t, organizer, starter, viewer, outsider, admin, meetingId, Guid.Empty, new MeetingRecordingWorkerTests.Nas(), lk);
        var rec = (await Svc(w).StartAsync(starter.Id, "kalosi", default)).Recording!;
        using (var db = t.Verify())
        {
            var row = db.MeetingRecordings.Single();
            row.Status = MeetingRecordingStatus.Ready; row.NasPath = $"/nas/rec/{rec.Id}.mp4"; row.EndedAt = DateTime.UtcNow;
            db.SaveChanges();
        }
        t.Db.ChangeTracker.Clear();   // the service context still tracks the row as Recording
        return w with { RecordingId = rec.Id };
    }

    private static ClaimsPrincipal Jwt(User u, params string[] roles) => new(new ClaimsIdentity(
        new[] { new Claim("nik", u.Nik), new Claim("sub", u.Id.ToString()) }.Concat(roles.Select(r => new Claim(ClaimTypes.Role, r))), "Bearer"));
    private static int Status(IResult r) => r is IStatusCodeHttpResult s ? s.StatusCode ?? 200 : 200;

    private Task<IResult> Delete(World w, User u, params string[] roles) =>
        MeetingRoomEndpoints.DeleteRecordingAsync(w.RecordingId, new DefaultHttpContext { User = Jwt(u, roles) }, w.T.Db, Svc(w), default);

    [Fact]
    public async Task Start_links_the_recording_to_the_booking_running_in_the_room()
    {
        var w = await SetupAsync(); using var t = w.T;
        Assert.Equal(w.MeetingId, (await t.Verify().MeetingRecordings.SingleAsync()).MeetingId);
    }

    [Fact]
    public async Task Ad_hoc_recording_has_no_booking()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-toraja"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        await new MeetingRecordingService(t.Db, lk, new MeetingRecordingServiceTests.FakeEgress(), new DbSettings(t.Db), Cfg())
            .StartAsync(a.Id, "toraja", default);
        Assert.Null((await t.Verify().MeetingRecordings.SingleAsync()).MeetingId);
    }

    [Fact]
    public async Task Admin_who_was_not_present_sees_it_only_in_the_admin_list()
    {
        var w = await SetupAsync(); using var t = w.T;
        var svc = Svc(w);
        Assert.Empty(await svc.ListAsync(w.Admin.Id, true, default));
        var all = Assert.Single(await svc.ListAllAsync(default));
        Assert.True(all.CanDelete);
        Assert.Equal(0, all.ChatCount);
        // Viewing for audit from Administration still works.
        Assert.True(await svc.CanViewAsync(w.Admin.Id, true, w.RecordingId, default));
        // The organizer was not in the room either: no row in their list, although they could delete it.
        Assert.Empty(await svc.ListAsync(w.Organizer.Id, false, default));
    }

    [Fact]
    public async Task List_shows_can_delete_per_user()
    {
        var w = await SetupAsync(); using var t = w.T;
        using (var db = t.Verify())
        {
            db.MeetingRecordingViewers.Add(new MeetingRecordingViewer { RecordingId = w.RecordingId, UserId = w.Organizer.Id });
            db.MeetingRecordingViewers.Add(new MeetingRecordingViewer { RecordingId = w.RecordingId, UserId = w.Admin.Id });
            db.SaveChanges();
        }
        var svc = Svc(w);
        Assert.True(Assert.Single(await svc.ListAsync(w.Starter.Id, false, default)).CanDelete);
        Assert.True(Assert.Single(await svc.ListAsync(w.Organizer.Id, false, default)).CanDelete);
        Assert.False(Assert.Single(await svc.ListAsync(w.Viewer.Id, false, default)).CanDelete);
        Assert.True(Assert.Single(await svc.ListAsync(w.Admin.Id, true, default)).CanDelete);   // present admin: listed once
    }

    [Fact]
    public async Task Viewer_who_may_not_delete_gets_403_and_outsider_gets_404()
    {
        var w = await SetupAsync(); using var t = w.T;
        Assert.Equal(403, Status(await Delete(w, w.Viewer)));
        Assert.Equal(404, Status(await Delete(w, w.Outsider)));   // never reveals that it exists
        Assert.Empty(w.Nas.Deleted);
        Assert.Equal(MeetingRecordingStatus.Ready, (await t.Verify().MeetingRecordings.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("starter")]
    [InlineData("organizer")]
    [InlineData("admin")]
    public async Task Starter_organizer_or_admin_deletes_and_the_nas_file_goes_right_away(string who)
    {
        var w = await SetupAsync(); using var t = w.T;
        var cache = Path.Combine(_dir, "cache"); Directory.CreateDirectory(cache);
        var cached = Path.Combine(cache, w.RecordingId.ToString("N") + ".mp4"); File.WriteAllBytes(cached, [1, 2, 3]);
        var r = who switch
        {
            "starter" => await Delete(w, w.Starter),
            "organizer" => await Delete(w, w.Organizer),
            _ => await Delete(w, w.Admin, "Admin"),
        };
        Assert.Equal(204, Status(r));
        Assert.Equal($"/nas/rec/{w.RecordingId}.mp4", Assert.Single(w.Nas.Deleted));
        var row = await t.Verify().MeetingRecordings.SingleAsync();
        Assert.Equal(MeetingRecordingStatus.Deleted, row.Status);
        Assert.Null(row.NasPath);
        Assert.False(File.Exists(cached));
        Assert.Equal(404, Status(await Delete(w, w.Starter)));   // already gone
    }

    [Fact]
    public async Task Organizer_rights_follow_the_booking_not_the_attendee_list()
    {
        var w = await SetupAsync(); using var t = w.T;
        using (var db = t.Verify()) { db.Meetings.Single().OrganizerId = w.Viewer.Id; db.SaveChanges(); }
        Assert.Equal(404, Status(await Delete(w, w.Organizer)));   // no longer organizer and never a viewer
        Assert.Equal(204, Status(await Delete(w, w.Viewer)));
    }

    [Fact]
    public async Task Running_recording_cannot_be_deleted()
    {
        var w = await SetupAsync(); using var t = w.T;
        using (var db = t.Verify()) { db.MeetingRecordings.Single().Status = MeetingRecordingStatus.Recording; db.SaveChanges(); }
        t.Db.ChangeTracker.Clear();
        Assert.Equal(409, Status(await Delete(w, w.Starter)));
        Assert.Empty(w.Nas.Deleted);
    }

    [Fact]
    public async Task Nas_failure_keeps_the_row_deleted_and_the_nightly_purge_retries()
    {
        var w = await SetupAsync(); using var t = w.T;
        w.Nas.FailDeletePath = $"/nas/rec/{w.RecordingId}.mp4";
        Assert.Equal(204, Status(await Delete(w, w.Starter)));
        var row = await t.Verify().MeetingRecordings.SingleAsync();
        Assert.Equal((MeetingRecordingStatus.Deleted, $"/nas/rec/{w.RecordingId}.mp4"), (row.Status, row.NasPath));
        Assert.Empty(await Svc(w).ListAllAsync(default));

        w.Nas.FailDeletePath = null;
        var sc = new ServiceCollection();
        sc.AddScoped(_ => t.Verify());
        sc.AddScoped<IPop.Application.Settings.ISettings>(p => new DbSettings(p.GetRequiredService<IPop.Infrastructure.Persistence.IPopDbContext>()));
        sc.AddSingleton<IPop.Application.Files.IFileAccessService>(w.Nas);
        using var sp = sc.BuildServiceProvider();
        Assert.Equal(1, await MeetingRecordingWorker.PurgeAsync(sp, DateTime.UtcNow, default));
        Assert.Equal($"/nas/rec/{w.RecordingId}.mp4", Assert.Single(w.Nas.Deleted));
        Assert.Null((await t.Verify().MeetingRecordings.SingleAsync()).NasPath);
    }

    /// Review Focus 5: someone deletes it while another person plays it. The player's next range request and any new
    /// ticket answer 404 (no 500), so the client can say "This recording was deleted".
    [Fact]
    public async Task Player_and_ticket_after_delete_are_404()
    {
        var w = await SetupAsync(); using var t = w.T;
        var signer = new RecordingTicketSigner(Secret);
        var ticket = signer.Create(w.RecordingId, w.Viewer.Id);
        var cfg = Cfg();
        Task<IResult> File() => MeetingRoomEndpoints.FileAsync(w.RecordingId, ticket, null, new DefaultHttpContext(), t.Db, Svc(w), signer,
            new PermissionEvaluator(t.Db), new RecordingFileCache(w.Nas, cfg), default);
        Assert.Equal(200, Status(await File()));   // playing

        Assert.Equal(204, Status(await Delete(w, w.Starter)));

        Assert.Equal(404, Status(await File()));
        Assert.Equal(404, Status(await MeetingRoomEndpoints.TicketAsync(w.RecordingId, new DefaultHttpContext { User = Jwt(w.Viewer) }, t.Db,
            Svc(w), signer, default)));
        Assert.Equal(404, Status(await MeetingRoomEndpoints.TicketAsync(w.RecordingId, new DefaultHttpContext { User = Jwt(w.Admin, "Admin") }, t.Db,
            Svc(w), signer, default)));
        Assert.False(System.IO.File.Exists(Path.Combine(_dir, "cache", w.RecordingId.ToString("N") + ".mp4")));
    }
}
