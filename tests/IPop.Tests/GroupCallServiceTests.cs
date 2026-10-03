using IPop.Application.Calls;
using IPop.Infrastructure.Calls;
using Xunit;

namespace IPop.Tests;

/// Group calls (Kind "group", LiveKit media): one live call per chat group, members ring,
/// late join, per-member decline/leave, last-out ends, non-joiners get "missed".
public class GroupCallServiceTests
{
    private static CallService Svc(TestDb t) => new(t.Verify());

    private static (TestDb t, Core.Entities.User a, Core.Entities.User b, Core.Entities.User c, Core.Entities.ChatRoom g) Group()
    {
        var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var c = t.AddUser("C");
        var g = t.AddRoom("group", "Tim Proyek");
        t.AddMember(g.Id, a.Id, "owner");
        t.AddMember(g.Id, b.Id);
        t.AddMember(g.Id, c.Id);
        return (t, a, b, c, g);
    }

    [Fact]
    public async Task StartGroupCall_InvitesEveryMember_StarterAccepted()
    {
        var (t, a, b, c, g) = Group();
        using var _ = t;
        var res = await Svc(t).StartGroupCallAsync(a.Id, g.Id, default);

        Assert.True(res.Success);
        var room = res.Room!;
        Assert.Equal(CallKind.Group, room.Kind);
        Assert.Equal(g.Id, room.ChatRoomId);
        Assert.Equal("Tim Proyek", room.Title);
        Assert.Equal(CallStatus.Ringing, room.Status);
        Assert.Equal(3, room.Participants.Count);
        Assert.Equal("accepted", room.Participants.Single(p => p.UserId == a.Id).Status);
        Assert.All(room.Participants.Where(p => p.UserId != a.Id), p => Assert.Equal("invited", p.Status));

        // Members see the ring; the starter does not.
        Assert.Equal(room.Id, (await Svc(t).GetRingingCallAsync(b.Id, default))!.Id);
        Assert.Null(await Svc(t).GetRingingCallAsync(a.Id, default));
    }

    [Fact]
    public async Task SecondStart_ReturnsSameCall_AndJoins()
    {
        var (t, a, b, _, g) = Group();
        using var _t = t;
        var first = (await Svc(t).StartGroupCallAsync(a.Id, g.Id, default)).Room!;
        var second = await Svc(t).StartGroupCallAsync(b.Id, g.Id, default);

        Assert.True(second.Success);
        Assert.Equal(first.Id, second.Room!.Id);
        Assert.Equal(CallStatus.Active, second.Room.Status);
        Assert.Equal("accepted", second.Room.Participants.Single(p => p.UserId == b.Id).Status);
    }

    [Fact]
    public async Task NonMember_IsForbidden_ForStartJoinAndActive()
    {
        var (t, a, _, _, g) = Group();
        using var _t = t;
        var outsider = t.AddUser("X");
        var start = await Svc(t).StartGroupCallAsync(outsider.Id, g.Id, default);
        Assert.Equal(CallErrors.NotGroupMember, start.ErrorCode);

        var call = (await Svc(t).StartGroupCallAsync(a.Id, g.Id, default)).Room!;
        Assert.Equal(CallErrors.NotParticipant, (await Svc(t).JoinGroupCallAsync(outsider.Id, call.Id, default)).ErrorCode);
        Assert.Equal(CallErrors.NotGroupMember, (await Svc(t).GetActiveGroupCallAsync(outsider.Id, g.Id, default)).ErrorCode);
    }

    [Fact]
    public async Task StartOnDm_IsGroupNotFound()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var dm = t.AddRoom("dm");
        t.AddMember(dm.Id, a.Id);
        Assert.Equal(CallErrors.GroupNotFound, (await Svc(t).StartGroupCallAsync(a.Id, dm.Id, default)).ErrorCode);
    }

    [Fact]
    public async Task Join_MakesCallActive_AndActiveEndpointReportsIt()
    {
        var (t, a, b, c, g) = Group();
        using var _t = t;
        var call = (await Svc(t).StartGroupCallAsync(a.Id, g.Id, default)).Room!;
        var joined = await Svc(t).JoinGroupCallAsync(b.Id, call.Id, default);

        Assert.True(joined.Success);
        Assert.Equal(CallStatus.Active, joined.Room!.Status);
        Assert.NotNull(joined.Room.EstablishedAt);

        var active = await Svc(t).GetActiveGroupCallAsync(c.Id, g.Id, default);
        Assert.True(active.Success);
        Assert.Equal(call.Id, active.Room!.Id);
        Assert.Equal(2, active.Room.Participants.Count(p => p.Status == "accepted"));
    }

    [Fact]
    public async Task Decline_OnlyAffectsThatMember()
    {
        var (t, a, b, c, g) = Group();
        using var _t = t;
        var call = (await Svc(t).StartGroupCallAsync(a.Id, g.Id, default)).Room!;
        var res = await Svc(t).SignalAsync(b.Id, new CallSignalRequest(call.Id, "decline"), default);

        Assert.True(res.Success);
        Assert.Equal(CallStatus.Ringing, res.Room!.Status);
        Assert.Equal("declined", res.Room.Participants.Single(p => p.UserId == b.Id).Status);
        Assert.Equal("invited", res.Room.Participants.Single(p => p.UserId == c.Id).Status);
    }

    [Fact]
    public async Task LeaveLast_EndsCall_NonJoinersMissed()
    {
        var (t, a, b, c, g) = Group();
        using var _t = t;
        var call = (await Svc(t).StartGroupCallAsync(a.Id, g.Id, default)).Room!;
        await Svc(t).JoinGroupCallAsync(b.Id, call.Id, default);

        // Starter hangs up: the call continues for B.
        var afterA = await Svc(t).SignalAsync(a.Id, new CallSignalRequest(call.Id, "end"), default);
        Assert.Equal(CallStatus.Active, afterA.Room!.Status);
        Assert.Equal("left", afterA.Room.Participants.Single(p => p.UserId == a.Id).Status);

        var afterB = await Svc(t).SignalAsync(b.Id, new CallSignalRequest(call.Id, "leave"), default);
        Assert.Equal(CallStatus.Ended, afterB.Room!.Status);

        var ha = (await Svc(t).GetHistoryAsync(a.Id, 1, 50, default)).Items.Single();
        Assert.Equal(("outgoing", "completed", "Tim Proyek", g.Id), (ha.Direction, ha.Result, ha.PeerName, ha.ChatRoomId));
        Assert.Null(ha.PeerNik);
        Assert.Equal("completed", (await Svc(t).GetHistoryAsync(b.Id, 1, 50, default)).Items.Single().Result);
        var hc = (await Svc(t).GetHistoryAsync(c.Id, 1, 50, default)).Items.Single();
        Assert.Equal(("incoming", "missed"), (hc.Direction, hc.Result));
        Assert.Null((await Svc(t).GetActiveGroupCallAsync(c.Id, g.Id, default)).Room);
    }

    [Fact]
    public async Task LateJoiner_AfterRingWindow_CanStillJoin()
    {
        var (t, a, b, c, g) = Group();
        using var _t = t;
        var call = (await Svc(t).StartGroupCallAsync(a.Id, g.Id, default)).Room!;
        await Svc(t).JoinGroupCallAsync(b.Id, call.Id, default);
        using (var db = t.Verify())
        {
            db.CallRooms.Single(r => r.Id == call.Id).StartedAt = DateTime.UtcNow.AddSeconds(-120);
            db.SaveChanges();
        }
        // The sweep only closes calls still ringing; an active group call stays open.
        Assert.Empty(await Svc(t).ExpireRingingAsync(DateTime.UtcNow - CallService.RingTimeout, default));
        Assert.Null(await Svc(t).GetRingingCallAsync(c.Id, default)); // no longer rings C

        var late = await Svc(t).JoinGroupCallAsync(c.Id, call.Id, default);
        Assert.True(late.Success);
        Assert.Equal(3, late.Room!.Participants.Count(p => p.Status == "accepted"));
    }

    [Fact]
    public async Task NobodyJoins_TimesOut_AsMissed()
    {
        var (t, a, b, _, g) = Group();
        using var _t = t;
        var call = (await Svc(t).StartGroupCallAsync(a.Id, g.Id, default)).Room!;
        using (var db = t.Verify())
        {
            db.CallRooms.Single(r => r.Id == call.Id).StartedAt = DateTime.UtcNow.AddSeconds(-60);
            db.SaveChanges();
        }
        var expired = await Svc(t).ExpireRingingAsync(DateTime.UtcNow - CallService.RingTimeout, default);

        Assert.Equal(CallStatus.Missed, Assert.Single(expired).Status);
        Assert.Equal("no-answer", (await Svc(t).GetHistoryAsync(a.Id, 1, 50, default)).Items.Single().Result);
        Assert.Equal("missed", (await Svc(t).GetHistoryAsync(b.Id, 1, 50, default)).Items.Single().Result);
    }

    [Fact]
    public async Task DroppedConnection_LeavesGroupCall_WithoutEndingIt()
    {
        var (t, a, b, _, g) = Group();
        using var _t = t;
        var call = (await Svc(t).StartGroupCallAsync(a.Id, g.Id, default)).Room!;
        await Svc(t).JoinGroupCallAsync(b.Id, call.Id, default);

        var rooms = await Svc(t).EndForUserAsync(a.Id, default);
        Assert.Equal(CallStatus.Active, Assert.Single(rooms).Status);
        Assert.Equal(call.Id, (await Svc(t).GetActiveGroupCallAsync(b.Id, g.Id, default)).Room!.Id);
    }

    [Fact]
    public void LiveKitRoomName_UsesCallPrefixAndN()
    {
        var id = Guid.NewGuid();
        Assert.Equal("call-" + id.ToString("N"), CallService.LiveKitRoomName(id));
    }
}

public class GroupCallRejoinTests
{
    [Fact]
    public async Task Member_who_left_the_group_cannot_rejoin_its_call()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var g = t.AddRoom("group", "Tim");
        t.AddMember(g.Id, a.Id, "owner");
        var mb = t.AddMember(g.Id, b.Id);
        var call = (await new CallService(t.Verify()).StartGroupCallAsync(a.Id, g.Id, default)).Room!;

        mb.LeftAt = DateTime.UtcNow;
        await t.Db.SaveChangesAsync();
        Assert.Equal(CallErrors.NotParticipant, (await new CallService(t.Verify()).JoinGroupCallAsync(b.Id, call.Id, default)).ErrorCode);
    }
}
