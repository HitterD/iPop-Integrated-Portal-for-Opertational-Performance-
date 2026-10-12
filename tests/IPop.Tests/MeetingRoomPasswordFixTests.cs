using IPop.Api.Endpoints;
using IPop.Core.Entities;
using IPop.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Api.Endpoints.CalendarDelegateEndpoints;

namespace IPop.Tests;

/// Task 8 review fix round 1 (server): a delegate answering an invite never gets the room password (I-2), and a hall-room
/// meeting created from a mail invitation still sends the invite notification with the password (M-7).
public class MeetingRoomPasswordFixTests
{
    private sealed class Gateway : INotificationGatewayService
    {
        public List<(string EventType, string Message, NotificationTarget Target)> Calls { get; } = [];
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Calls.Add((eventType, message, target));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }
        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private static MeetingEndpoints.CreateMeetingRequest Req(string location, IReadOnlyList<string> niks, Guid? mail = null) =>
        new("Weekly ICT sync", DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(1), null, location, null, null, null, 15, null, niks, mail, null);

    [Fact]
    public async Task Delegate_rsvp_answer_carries_no_room_password_but_the_owner_still_sees_it()
    {
        using var t = new TestDb();
        var boss = t.AddUser("PFX_B"); var pa = t.AddUser("PFX_P"); var org = t.AddUser("PFX_O");
        await GrantAsync(t.Db, null, null, boss.Id, "PFX_P", DelegateLevel.Full, boss.Id, default);
        var (_, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req("SJA Hall · Toraja", ["PFX_B"]), default);

        var (owner, actedBy, denied) = await ResolveOwnerAsync(t.Db, pa.Id, "PFX_B", DelegateLevel.Full, default);
        Assert.Null(denied);
        Assert.Equal(MeetingEndpoints.RsvpResult.Applied, await MeetingEndpoints.ApplyRsvpAsync(t.Db, id!.Value, owner, "accept", null, default, actedBy));

        var forDelegate = await MeetingEndpoints.RsvpDtoAsync(t.Db, id.Value, owner, actedBy, default);
        Assert.NotNull(forDelegate);
        Assert.Equal("accept", forDelegate!.MyResponse);
        Assert.Null(forDelegate.RoomPassword);

        var forBoss = await MeetingEndpoints.RsvpDtoAsync(t.Db, id.Value, boss.Id, null, default);
        Assert.Matches("^[0-9]{6}$", forBoss!.RoomPassword);
    }

    [Fact]
    public async Task Hall_room_meeting_from_a_mail_invitation_still_sends_the_invite_with_the_password()
    {
        using var t = new TestDb();
        var org = t.AddUser("PFM_O"); var a = t.AddUser("PFM_A");
        var (_, id, invited) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req("SJA Hall · Kalosi", ["PFM_A"], Guid.NewGuid()), default);
        await using var v = t.Verify();
        var dto = (await MeetingEndpoints.LoadUpcomingAsync(v, org.Id, DateTime.UtcNow, default)).Single();
        var gw = new Gateway();

        await MeetingEndpoints.PublishCreatedAsync(t.Db, gw, dto, invited, sourceMailId: Guid.NewGuid(), null, default);

        var call = Assert.Single(gw.Calls);
        Assert.Equal("meeting.invited", call.EventType);
        Assert.Equal([a.Id], call.Target.UserIds);
        Assert.Contains($"Room password: {dto.RoomPassword![..3]} {dto.RoomPassword[3..]}", call.Message);
    }

    [Fact]
    public async Task Other_meetings_from_a_mail_invitation_stay_covered_by_the_mail_alone()
    {
        using var t = new TestDb();
        var org = t.AddUser("PFN_O"); t.AddUser("PFN_A");
        var (_, _, invited) = await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req("Online · iPop Meeting", ["PFN_A"], Guid.NewGuid()), default);
        await using var v = t.Verify();
        var dto = (await MeetingEndpoints.LoadUpcomingAsync(v, org.Id, DateTime.UtcNow, default)).Single();
        var gw = new Gateway();

        await MeetingEndpoints.PublishCreatedAsync(t.Db, gw, dto, invited, sourceMailId: Guid.NewGuid(), null, default);
        Assert.Empty(gw.Calls);

        // Without a mail the form path is unchanged: invite (no password line for a non-hall place).
        await MeetingEndpoints.PublishCreatedAsync(t.Db, gw, dto, invited, sourceMailId: null, null, default);
        Assert.Contains(gw.Calls, c => c.EventType == "meeting.invited" && !c.Message.Contains("password"));
    }
}
