using IPop.Application.Calls;
using IPop.Infrastructure.Calls;
using Xunit;

namespace IPop.Tests;

/// "Add person" to a running call: invite auth, re-invite, the 1:1 → ad-hoc LiveKit upgrade,
/// join permission for invitees, per-invitee ring window, ad-hoc ending and history labels.
public class CallAddPersonTests
{
    private static CallService Svc(TestDb t) => new(t.Verify());

    /// A called B and B answered: an active 1:1 P2P call.
    private static async Task<(TestDb t, Core.Entities.User a, Core.Entities.User b, Guid callId)> ActiveOneToOne()
    {
        var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = (await Svc(t).CreateCallAsync(a.Id, new CreateCallRequest("B"), default)).Room!;
        await Svc(t).SignalAsync(b.Id, new CallSignalRequest(call.Id, "accept"), default);
        return (t, a, b, call.Id);
    }

    [Fact]
    public async Task Invite_UpgradesOneToOne_ToAdHoc_AndRingsInvitee()
    {
        var (t, a, b, id) = await ActiveOneToOne();
        using var _ = t;
        var c = t.AddUser("C");

        var res = await Svc(t).InviteAsync(b.Id, id, "C", default);

        Assert.True(res.Success, res.Error);
        var room = res.Room!;
        Assert.Equal(CallKind.AdHoc, room.Kind);
        Assert.Equal(CallStatus.Active, room.Status);
        Assert.Null(room.ChatRoomId);
        var invitee = room.Participants.Single(p => p.UserId == c.Id);
        Assert.Equal(CallParticipantStatus.Invited, invitee.Status);
        Assert.Equal(b.Id, invitee.InvitedById);
        // Both original participants stay in the call.
        Assert.Equal(2, room.Participants.Count(p => p.Status == CallParticipantStatus.Accepted));

        var ringing = await Svc(t).GetRingingCallAsync(c.Id, default);
        Assert.Equal(id, ringing!.Id);

        // A second invite keeps it ad-hoc (never back to P2P).
        t.AddUser("D");
        Assert.Equal(CallKind.AdHoc, (await Svc(t).InviteAsync(a.Id, id, "D", default)).Room!.Kind);
    }

    [Fact]
    public async Task Invite_RequiresAcceptedParticipant()
    {
        var (t, a, b, id) = await ActiveOneToOne();
        using var _ = t;
        var outsider = t.AddUser("X");
        t.AddUser("C");

        Assert.Equal(CallErrors.NotParticipant, (await Svc(t).InviteAsync(outsider.Id, id, "C", default)).ErrorCode);

        // Someone who left the call can't add people either.
        await Svc(t).InviteAsync(a.Id, id, "C", default);
        await Svc(t).SignalAsync(b.Id, new CallSignalRequest(id, "leave"), default);
        Assert.Equal(CallErrors.NotParticipant, (await Svc(t).InviteAsync(b.Id, id, "X", default)).ErrorCode);
    }

    [Fact]
    public async Task Invite_RejectsRingingOneToOne_UnknownPeople_AndPeopleAlreadyIn()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        t.AddUser("B");
        t.AddUser("C");
        var ringing = (await Svc(t).CreateCallAsync(a.Id, new CreateCallRequest("B"), default)).Room!;
        Assert.Equal(CallErrors.InvalidSignal, (await Svc(t).InviteAsync(a.Id, ringing.Id, "C", default)).ErrorCode);

        var (t2, a2, _, id) = await ActiveOneToOne();
        using var _t2 = t2;
        t2.AddUser("C");
        Assert.Equal(CallErrors.PeerNotFound, (await Svc(t2).InviteAsync(a2.Id, id, "nobody", default)).ErrorCode);
        Assert.Equal(CallErrors.AlreadyInCall, (await Svc(t2).InviteAsync(a2.Id, id, "B", default)).ErrorCode);
        Assert.True((await Svc(t2).InviteAsync(a2.Id, id, "C", default)).Success);
        // Still being rung: no double ring.
        Assert.Equal(CallErrors.AlreadyInCall, (await Svc(t2).InviteAsync(a2.Id, id, "C", default)).ErrorCode);
    }

    [Fact]
    public async Task ReInvite_AfterDeclineOrLeave_RingsAgain()
    {
        var (t, a, b, id) = await ActiveOneToOne();
        using var _ = t;
        var c = t.AddUser("C");
        await Svc(t).InviteAsync(a.Id, id, "C", default);
        await Svc(t).SignalAsync(c.Id, new CallSignalRequest(id, "decline"), default);
        Assert.Null(await Svc(t).GetRingingCallAsync(c.Id, default));

        var again = await Svc(t).InviteAsync(b.Id, id, "C", default);
        Assert.True(again.Success, again.Error);
        var p = again.Room!.Participants.Single(x => x.UserId == c.Id);
        Assert.Equal((CallParticipantStatus.Invited, (Guid?)b.Id), (p.Status, p.InvitedById));
        Assert.Single(again.Room.Participants, x => x.UserId == c.Id); // same row, no duplicate
        Assert.NotNull(await Svc(t).GetRingingCallAsync(c.Id, default));

        // Joined, then left: may be rung back in.
        await Svc(t).JoinGroupCallAsync(c.Id, id, default);
        await Svc(t).SignalAsync(c.Id, new CallSignalRequest(id, "leave"), default);
        Assert.True((await Svc(t).InviteAsync(a.Id, id, "C", default)).Success);
    }

    [Fact]
    public async Task MissedInvite_TimesOutPerInvitee_WithoutEndingTheCall()
    {
        var (t, a, _, id) = await ActiveOneToOne();
        using var _ = t;
        var c = t.AddUser("C");
        await Svc(t).InviteAsync(a.Id, id, "C", default);
        using (var db = t.Verify())
        {
            db.CallParticipants.Single(p => p.CallId == id && p.UserId == c.Id).JoinedAt = DateTime.UtcNow.AddSeconds(-60);
            db.SaveChanges();
        }
        Assert.Null(await Svc(t).GetRingingCallAsync(c.Id, default));
        Assert.Empty(await Svc(t).ExpireRingingAsync(DateTime.UtcNow - CallService.RingTimeout, default));
        Assert.Equal(CallStatus.Active, (await Svc(t).GetRoomAsync(id, a.Id, default))!.Status);
        // The ring window is over, so a fresh invite is allowed.
        Assert.True((await Svc(t).InviteAsync(a.Id, id, "C", default)).Success);
    }

    [Fact]
    public async Task AdHocInvitee_CanJoin_OutsiderCannot()
    {
        var (t, a, _, id) = await ActiveOneToOne();
        using var _ = t;
        var c = t.AddUser("C");
        var x = t.AddUser("X");
        await Svc(t).InviteAsync(a.Id, id, "C", default);

        var joined = await Svc(t).JoinGroupCallAsync(c.Id, id, default);
        Assert.True(joined.Success, joined.Error);
        Assert.Equal(3, joined.Room!.Participants.Count(p => p.Status == CallParticipantStatus.Accepted));
        Assert.Equal(CallErrors.NotParticipant, (await Svc(t).JoinGroupCallAsync(x.Id, id, default)).ErrorCode);
    }

    [Fact]
    public async Task GroupCall_OutsiderInvitee_JoinsCallOnly_AndHistoryShowsGroupName()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var outsider = t.AddUser("O");
        var g = t.AddRoom("group", "ICT IPOP");
        t.AddMember(g.Id, a.Id, "owner");
        t.AddMember(g.Id, b.Id);
        var call = (await Svc(t).StartGroupCallAsync(a.Id, g.Id, default)).Room!;

        var invited = await Svc(t).InviteAsync(a.Id, call.Id, "O", default);
        Assert.True(invited.Success, invited.Error);
        Assert.Equal(CallKind.Group, invited.Room!.Kind);
        Assert.Equal(call.Id, (await Svc(t).GetRingingCallAsync(outsider.Id, default))!.Id);

        var joined = await Svc(t).JoinGroupCallAsync(outsider.Id, call.Id, default);
        Assert.True(joined.Success, joined.Error);
        Assert.Equal(CallStatus.Active, joined.Room!.Status);
        // Not added to the chat group.
        Assert.False(t.Verify().ChatRoomMembers.Any(m => m.RoomId == g.Id && m.UserId == outsider.Id));
        Assert.Equal(CallErrors.NotGroupMember, (await Svc(t).GetActiveGroupCallAsync(outsider.Id, g.Id, default)).ErrorCode);

        await Svc(t).SignalAsync(a.Id, new CallSignalRequest(call.Id, "leave"), default);
        await Svc(t).SignalAsync(outsider.Id, new CallSignalRequest(call.Id, "leave"), default);

        var h = (await Svc(t).GetHistoryAsync(outsider.Id, 1, 50, default)).Items.Single();
        Assert.Equal(("ICT IPOP", "completed"), (h.PeerName, h.Result));
    }

    [Fact]
    public async Task InviteToRingingGroupCall_KeepsItAliveForTheInviteeRing()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        t.AddUser("O");
        var g = t.AddRoom("group", "G");
        t.AddMember(g.Id, a.Id, "owner");
        t.AddMember(g.Id, b.Id);
        var call = (await Svc(t).StartGroupCallAsync(a.Id, g.Id, default)).Room!;
        using (var db = t.Verify())
        {
            db.CallRooms.Single(r => r.Id == call.Id).StartedAt = DateTime.UtcNow.AddSeconds(-40);
            db.SaveChanges();
        }
        await Svc(t).InviteAsync(a.Id, call.Id, "O", default);
        using (var db = t.Verify())
        {
            db.CallRooms.Single(r => r.Id == call.Id).StartedAt = DateTime.UtcNow.AddSeconds(-60);
            db.SaveChanges();
        }
        // The group's own ring window is over, but the added person is still being rung.
        Assert.Empty(await Svc(t).ExpireRingingAsync(DateTime.UtcNow - CallService.RingTimeout, default));
    }

    [Fact]
    public async Task AdHocCall_EndsWhenLastAcceptedLeaves_PendingInviteeMissed_HistoryLabelled()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var c = t.AddUser("C");
        var d = t.AddUser("D");
        using (var db = t.Verify())
        {
            db.Users.Single(u => u.Id == a.Id).Name = "Beny Santoso";
            db.Users.Single(u => u.Id == b.Id).Name = "Jason Tan";
            db.Users.Single(u => u.Id == c.Id).Name = "Citra Dewi";
            db.Users.Single(u => u.Id == d.Id).Name = "Dodi Putra";
            db.SaveChanges();
        }
        var id = (await Svc(t).CreateCallAsync(a.Id, new CreateCallRequest("B"), default)).Room!.Id;
        await Svc(t).SignalAsync(b.Id, new CallSignalRequest(id, "accept"), default);
        await Svc(t).InviteAsync(a.Id, id, "C", default);
        await Svc(t).JoinGroupCallAsync(c.Id, id, default);
        await Svc(t).InviteAsync(c.Id, id, "D", default);   // D never answers

        // "end" from one person only takes them out in an ad-hoc call.
        var afterA = await Svc(t).SignalAsync(a.Id, new CallSignalRequest(id, "end"), default);
        Assert.Equal(CallStatus.Active, afterA.Room!.Status);
        await Svc(t).SignalAsync(b.Id, new CallSignalRequest(id, "leave"), default);
        var last = await Svc(t).SignalAsync(c.Id, new CallSignalRequest(id, "leave"), default);
        Assert.Equal(CallStatus.Ended, last.Room!.Status);
        Assert.Null(await Svc(t).GetRingingCallAsync(d.Id, default)); // pending ring stops

        var ha = (await Svc(t).GetHistoryAsync(a.Id, 1, 50, default)).Items.Single();
        Assert.Equal(("outgoing", "completed", "Call with Jason, Citra, +1"), (ha.Direction, ha.Result, ha.PeerName));
        Assert.Null(ha.PeerNik);
        Assert.Null(ha.ChatRoomId);
        var hc = (await Svc(t).GetHistoryAsync(c.Id, 1, 50, default)).Items.Single();
        Assert.Equal(("completed", "Call with Beny, Jason, +1"), (hc.Result, hc.PeerName));
        var hd = (await Svc(t).GetHistoryAsync(d.Id, 1, 50, default)).Items.Single();
        Assert.Equal(("incoming", "missed", "Call with Beny, Jason, +1"), (hd.Direction, hd.Result, hd.PeerName));
    }

    [Fact]
    public async Task PlainOneToOne_HistoryStillShowsThePeer()
    {
        var (t, a, b, id) = await ActiveOneToOne();
        using var _ = t;
        await Svc(t).SignalAsync(a.Id, new CallSignalRequest(id, "end"), default);
        var h = (await Svc(t).GetHistoryAsync(a.Id, 1, 50, default)).Items.Single();
        Assert.Equal((b.Name, "B"), (h.PeerName, h.PeerNik));
    }

    [Theory]
    [InlineData(new[] { "Jason Tan" }, "Call with Jason")]
    [InlineData(new[] { "Jason Tan", "Beny" }, "Call with Jason, Beny")]
    [InlineData(new[] { "Jason Tan", "Beny", "Citra", "Dodi" }, "Call with Jason, Beny, +2")]
    public void AdHocLabel_ListsTwoFirstNames(string[] names, string expected) =>
        Assert.Equal(expected, CallService.AdHocLabel(names));
}
