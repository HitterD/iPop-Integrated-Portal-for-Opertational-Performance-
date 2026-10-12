using System.Security.Claims;
using IPop.Api.Endpoints;
using IPop.Core.Entities;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Rbac;
using IPop.Infrastructure.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

/// Recording files are fetched with a ticket scoped to one recording and one user (I3), never a session JWT in the URL.
public class MeetingRecordingTicketTests : IDisposable
{
    private const string Secret = "test-secret-0123456789-0123456789-abcdef";
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "ipop-ticket-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_cache)) Directory.Delete(_cache, true); }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static int Status(IResult r) => r is IStatusCodeHttpResult s ? s.StatusCode ?? 200 : 200;

    [Fact]
    public void Ticket_verifies_only_for_its_recording_until_it_expires_and_not_when_tampered()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
        var signer = new RecordingTicketSigner(Secret, clock);
        var rec = Guid.NewGuid(); var user = Guid.NewGuid();
        var ticket = signer.Create(rec, user);

        Assert.Equal(user, signer.Verify(rec, ticket));
        Assert.Null(signer.Verify(Guid.NewGuid(), ticket));                                     // another recording
        Assert.Null(signer.Verify(rec, ticket.Replace(user.ToString("N"), Guid.NewGuid().ToString("N"))));  // another user
        var parts = ticket.Split('.');
        Assert.Null(signer.Verify(rec, $"{parts[0]}.{long.Parse(parts[1]) + 3600}.{parts[2]}"));  // longer expiry
        Assert.Null(signer.Verify(rec, ticket + "x"));
        Assert.Null(signer.Verify(rec, "garbage"));
        Assert.Null(signer.Verify(rec, null));
        Assert.Null(new RecordingTicketSigner("another-secret-xxxxxxxxxxxxxxxxxxxxxxxx", clock).Verify(rec, ticket));

        clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
        Assert.Equal(user, signer.Verify(rec, ticket));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(signer.Verify(rec, ticket));                                                  // expired after 6 h
    }

    private sealed record World(TestDb T, User Viewer, User Outsider, Guid RecordingId, Guid OtherRecordingId, string CacheFile);

    private World Setup()
    {
        var t = new TestDb();
        var role = new Role { Id = Guid.NewGuid(), Name = "Employee" };
        var perm = new Permission { Id = Guid.NewGuid(), Code = "calls.use" };
        t.Db.Roles.Add(role); t.Db.Permissions.Add(perm);
        t.Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
        var viewer = t.AddUser("VIEWER", mustChange: false); var outsider = t.AddUser("OUTSIDER", mustChange: false);
        t.Db.UserRoles.Add(new UserRole { UserId = viewer.Id, RoleId = role.Id });
        t.Db.UserRoles.Add(new UserRole { UserId = outsider.Id, RoleId = role.Id });
        MeetingRecording Rec(Guid viewerId)
        {
            var id = Guid.NewGuid();
            var r = new MeetingRecording
            {
                Id = id, RoomKey = "kalosi", Title = "Weekly sync", EgressId = "EG_1", StartedById = viewerId, StartedAt = DateTime.UtcNow.AddHours(-1),
                Status = MeetingRecordingStatus.Ready, NasPath = $"/n/{id}.mp4",
                Viewers = [new MeetingRecordingViewer { RecordingId = id, UserId = viewerId }],
            };
            t.Db.MeetingRecordings.Add(r);
            return r;
        }
        var rec = Rec(viewer.Id); var other = Rec(outsider.Id);
        t.Db.SaveChanges();
        Directory.CreateDirectory(_cache);
        var file = Path.Combine(_cache, rec.Id.ToString("N") + ".mp4");
        File.WriteAllBytes(file, [1, 2, 3]);
        return new(t, viewer, outsider, rec.Id, other.Id, file);
    }

    private Task<IResult> FileAsync(World w, Guid id, string? ticket, ClaimsPrincipal? user = null)
    {
        var http = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MeetingRooms:CacheDir"] = _cache }).Build();
        var recs = new MeetingRecordingService(w.T.Db, new MeetingRoomServiceTests.FakeRoomAdmin(), new MeetingRecordingServiceTests.FakeEgress(),
            new DbSettings(w.T.Db), cfg);
        return MeetingRoomEndpoints.FileAsync(id, ticket, null, http, w.T.Db, recs, new RecordingTicketSigner(Secret),
            new PermissionEvaluator(w.T.Db), new RecordingFileCache(new MeetingRecordingWorkerTests.Nas(), cfg), default);
    }

    private static ClaimsPrincipal Jwt(User u, params string[] roles) => new(new ClaimsIdentity(
        new[] { new Claim("nik", u.Nik), new Claim("sub", u.Id.ToString()) }.Concat(roles.Select(r => new Claim(ClaimTypes.Role, r))), "Bearer"));

    [Fact]
    public async Task Valid_ticket_serves_the_file_without_any_session()
    {
        var w = Setup(); using var t = w.T;
        var ticket = new RecordingTicketSigner(Secret).Create(w.RecordingId, w.Viewer.Id);
        var r = await FileAsync(w, w.RecordingId, ticket);
        Assert.Equal(200, Status(r));
        Assert.Equal(w.CacheFile, Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.PhysicalFileHttpResult>(r).FileName);
    }

    [Fact]
    public async Task Ticket_for_another_recording_tampered_or_missing_is_refused()
    {
        var w = Setup(); using var t = w.T;
        var signer = new RecordingTicketSigner(Secret);
        Assert.Equal(401, Status(await FileAsync(w, w.RecordingId, signer.Create(w.OtherRecordingId, w.Viewer.Id))));   // another recording
        Assert.Equal(401, Status(await FileAsync(w, w.RecordingId, signer.Create(w.RecordingId, w.Viewer.Id) + "A")));   // tampered
        Assert.Equal(401, Status(await FileAsync(w, w.RecordingId, new RecordingTicketSigner(Secret, new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(-7)))
            .Create(w.RecordingId, w.Viewer.Id))));                                                                      // expired
        Assert.Equal(401, Status(await FileAsync(w, w.RecordingId, null)));                                              // no ticket, no session
    }

    [Fact]
    public async Task Genuine_ticket_of_a_user_who_may_not_view_is_404_and_access_is_rechecked_on_every_request()
    {
        var w = Setup(); using var t = w.T;
        var signer = new RecordingTicketSigner(Secret);
        Assert.Equal(404, Status(await FileAsync(w, w.RecordingId, signer.Create(w.RecordingId, w.Outsider.Id))));

        var ticket = signer.Create(w.RecordingId, w.Viewer.Id);
        using (var db = w.T.Verify())
        {
            db.MeetingRecordings.Single(r => r.Id == w.RecordingId).Status = MeetingRecordingStatus.Deleted;
            db.SaveChanges();
        }
        Assert.Equal(404, Status(await FileAsync(w, w.RecordingId, ticket)));   // deleted after the ticket was issued
    }

    [Fact]
    public async Task Ticket_of_a_deactivated_account_is_refused()
    {
        var w = Setup(); using var t = w.T;
        var ticket = new RecordingTicketSigner(Secret).Create(w.RecordingId, w.Viewer.Id);
        using (var db = w.T.Verify()) { db.Users.Single(u => u.Id == w.Viewer.Id).IsActive = false; db.SaveChanges(); }
        Assert.Equal(401, Status(await FileAsync(w, w.RecordingId, ticket)));
    }

    [Fact]
    public async Task Authorization_header_still_works_without_a_ticket()
    {
        var w = Setup(); using var t = w.T;
        Assert.Equal(200, Status(await FileAsync(w, w.RecordingId, null, Jwt(w.Viewer))));
        Assert.Equal(404, Status(await FileAsync(w, w.RecordingId, null, Jwt(w.Outsider))));
        Assert.Equal(200, Status(await FileAsync(w, w.RecordingId, null, Jwt(w.Outsider, "Admin"))));
    }

    [Fact]
    public async Task Ticket_endpoint_issues_a_url_only_for_viewers()
    {
        var w = Setup(); using var t = w.T;
        var cfg = new ConfigurationBuilder().Build();
        var recs = new MeetingRecordingService(w.T.Db, new MeetingRoomServiceTests.FakeRoomAdmin(), new MeetingRecordingServiceTests.FakeEgress(),
            new DbSettings(w.T.Db), cfg);
        var signer = new RecordingTicketSigner(Secret);
        Task<IResult> Ticket(ClaimsPrincipal user, Guid id) =>
            MeetingRoomEndpoints.TicketAsync(id, new DefaultHttpContext { User = user }, w.T.Db, recs, signer, default);

        var ok = await Ticket(Jwt(w.Viewer), w.RecordingId);
        var url = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<MeetingRoomEndpoints.TicketResponse>>(ok).Value!.Url;
        Assert.StartsWith($"/api/meeting-rooms/recordings/{w.RecordingId}/file?t=", url);
        Assert.DoesNotContain("access_token", url);
        Assert.Equal(w.Viewer.Id, signer.Verify(w.RecordingId, Uri.UnescapeDataString(url[(url.IndexOf("?t=") + 3)..])));

        Assert.Equal(404, Status(await Ticket(Jwt(w.Outsider), w.RecordingId)));
        Assert.Equal(200, Status(await Ticket(Jwt(w.Outsider, "Admin"), w.RecordingId)));
        Assert.Equal(404, Status(await Ticket(Jwt(w.Viewer), Guid.NewGuid())));
    }

    [Theory]
    [InlineData("/hubs/app", true)]
    [InlineData("/hubs/calls", true)]
    [InlineData("/api/meeting-rooms/recordings/7f1c0a4e-0000-4000-8000-000000000001/file", false)]
    [InlineData("/api/meeting-rooms/recordings", false)]
    [InlineData("/api/files/x/file", false)]
    [InlineData("/api/me", false)]
    public void Query_token_is_accepted_only_for_hubs(string path, bool allowed) =>
        Assert.Equal(allowed, IPop.Api.QueryTokenRule.Allows(new PathString(path)));
}
