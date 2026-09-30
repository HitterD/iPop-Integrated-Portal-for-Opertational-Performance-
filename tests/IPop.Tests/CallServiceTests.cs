using IPop.Application.Calls;
using IPop.Infrastructure.Calls;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Calls stub state machine (ipop-09): invite -> ringing -> active/declined/missed -> ended,
/// mute/screen toggles, history rows with resolved peer names. Same seam as
/// NotificationGatewayServiceTests: SQLite in-memory via TestDb.
public class CallServiceTests
{
    private static CallService Svc(TestDb t) => new(t.Verify());

    [Fact]
    public async Task CreateCall_RingsPeer_WithInvitedParticipant()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var svc = Svc(t);

        var result = await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default);

        Assert.True(result.Success);
        Assert.Equal(CallStatus.Ringing, result.Room!.Status);
        Assert.Equal(2, result.Room.Participants.Count);
        Assert.Equal("accepted", result.Room.Participants.Single(p => p.Nik == "A").Status);
        Assert.Equal("invited", result.Room.Participants.Single(p => p.Nik == "B").Status);
        Assert.NotNull(result.Room.Token); // short-lived token stub present
    }

    [Fact]
    public async Task CreateCall_PeerNotFound_ReturnsError()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var svc = Svc(t);

        var result = await svc.CreateCallAsync(me.Id, new CreateCallRequest("nobody"), default);

        Assert.False(result.Success);
        Assert.Equal(CallErrors.PeerNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Accept_TransitionsToActive_WithEstablishedAt()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var svc = Svc(t);

        var call = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default)).Room!;
        var res = await svc.SignalAsync(peer.Id, new CallSignalRequest(call.Id, "accept"), default);

        Assert.True(res.Success);
        Assert.Equal(CallStatus.Active, res.Room!.Status);
        Assert.NotNull(res.Room.EstablishedAt);
        Assert.Equal("accepted", res.Room.Participants.Single(p => p.Nik == "B").Status);
    }

    [Fact]
    public async Task Decline_EndsCall_RecordsDeclinedHistory()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var svc = Svc(t);

        var call = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default)).Room!;
        var res = await svc.SignalAsync(peer.Id, new CallSignalRequest(call.Id, "decline"), default);

        Assert.True(res.Success);
        Assert.Equal(CallStatus.Declined, res.Room!.Status);

        // History: "declined" on both sides — the callee chose to, so it is not "missed".
        var hist = await svc.GetHistoryAsync(me.Id, 1, 50, default);
        Assert.Equal(1, hist.Total);
        Assert.Equal("outgoing", hist.Items[0].Direction);
        Assert.Equal("declined", hist.Items[0].Result);
        Assert.Equal("User B", hist.Items[0].PeerName);
        var peerHist = await svc.GetHistoryAsync(peer.Id, 1, 50, default);
        Assert.Equal(("incoming", "declined"), (peerHist.Items[0].Direction, peerHist.Items[0].Result));
    }

    [Fact]
    public async Task CallerCancelsWhileRinging_IsCanceledForCaller_MissedForCallee()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var svc = Svc(t);

        var call = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default)).Room!;
        var res = await svc.SignalAsync(me.Id, new CallSignalRequest(call.Id, "end"), default);

        Assert.Equal(CallStatus.Missed, res.Room!.Status);
        Assert.Equal("canceled", (await svc.GetHistoryAsync(me.Id, 1, 50, default)).Items.Single().Result);
        var peerRow = (await svc.GetHistoryAsync(peer.Id, 1, 50, default)).Items.Single();
        Assert.Equal(("incoming", "missed", 0), (peerRow.Direction, peerRow.Result, peerRow.DurationSeconds));
    }

    [Fact]
    public async Task RingTimeout_ExpiresOnlyOldRings_AsNoAnswerAndMissed()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var other = t.AddUser("C");
        var svc = Svc(t);

        var old = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default)).Room!;
        var fresh = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(other.Nik), default)).Room!;
        using (var db = t.Verify())
        {
            var room = db.CallRooms.Single(r => r.Id == old.Id);
            room.StartedAt = DateTime.UtcNow.AddSeconds(-60);
            db.SaveChanges();
        }

        // The stale ring no longer shows as incoming, even before the worker runs.
        Assert.Null(await Svc(t).GetRingingCallAsync(peer.Id, default));
        Assert.NotNull(await Svc(t).GetRingingCallAsync(other.Id, default));

        var expired = await Svc(t).ExpireRingingAsync(DateTime.UtcNow - CallService.RingTimeout, default);
        Assert.Equal(old.Id, Assert.Single(expired).Id);
        Assert.Equal(CallStatus.Missed, expired[0].Status);

        Assert.Equal("no-answer", (await Svc(t).GetHistoryAsync(me.Id, 1, 50, default)).Items.Single().Result);
        Assert.Equal("missed", (await Svc(t).GetHistoryAsync(peer.Id, 1, 50, default)).Items.Single().Result);
        Assert.Equal(CallStatus.Ringing, (await Svc(t).GetRoomAsync(fresh.Id, me.Id, default))!.Status);
    }

    [Fact]
    public async Task EndForUser_EndsActiveCall_AndCancelsOwnRings_ButNotRingsToThem()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var third = t.AddUser("C");
        var svc = Svc(t);

        var active = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default)).Room!;
        await svc.SignalAsync(peer.Id, new CallSignalRequest(active.Id, "accept"), default);
        var ringingToMe = (await Svc(t).CreateCallAsync(third.Id, new CreateCallRequest(me.Nik), default)).Room!;

        var ended = await Svc(t).EndForUserAsync(me.Id, default);

        Assert.Equal(active.Id, Assert.Single(ended).Id);
        Assert.Equal(CallStatus.Ended, ended[0].Status);
        Assert.Equal("left", ended[0].Participants.Single(p => p.UserId == me.Id).Status);
        Assert.Equal(CallStatus.Ringing, (await Svc(t).GetRoomAsync(ringingToMe.Id, me.Id, default))!.Status);
        Assert.Equal("completed", (await Svc(t).GetHistoryAsync(peer.Id, 1, 50, default)).Items.Single().Result);
    }

    [Fact]
    public async Task Duration_CountsFromPickup_NotFromRinging()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var svc = Svc(t);

        var call = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default)).Room!;
        await svc.SignalAsync(peer.Id, new CallSignalRequest(call.Id, "accept"), default);
        using (var db = t.Verify())
        {
            var room = db.CallRooms.Single(r => r.Id == call.Id);
            room.StartedAt = DateTime.UtcNow.AddSeconds(-100);   // rang for a while
            room.EstablishedAt = DateTime.UtcNow.AddSeconds(-30); // talked ~30 s
            db.SaveChanges();
        }
        await Svc(t).SignalAsync(me.Id, new CallSignalRequest(call.Id, "end"), default);

        var row = (await Svc(t).GetHistoryAsync(me.Id, 1, 50, default)).Items.Single();
        Assert.Equal("completed", row.Result);
        Assert.InRange(row.DurationSeconds, 29, 32);
    }

    [Fact]
    public async Task Leave_LastAccepted_EndsCall()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var svc = Svc(t);

        var call = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default)).Room!;
        await svc.SignalAsync(peer.Id, new CallSignalRequest(call.Id, "accept"), default);

        // Peer leaves; initiator remains → call stays active.
        var leave1 = await svc.SignalAsync(peer.Id, new CallSignalRequest(call.Id, "leave"), default);
        Assert.Equal(CallStatus.Active, leave1.Room!.Status);

        // Initiator leaves too → last one out ends it.
        var leave2 = await svc.SignalAsync(me.Id, new CallSignalRequest(call.Id, "leave"), default);
        Assert.Equal(CallStatus.Ended, leave2.Room!.Status);
    }

    [Fact]
    public async Task End_ByAnyParticipant_EndsCallImmediately()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var svc = Svc(t);

        var call = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default)).Room!;
        await svc.SignalAsync(peer.Id, new CallSignalRequest(call.Id, "accept"), default);

        // Either participant (e.g. peer/callee) sends "end" → room is immediately Ended for everyone.
        var endRes = await svc.SignalAsync(peer.Id, new CallSignalRequest(call.Id, "end"), default);
        Assert.True(endRes.Success);
        Assert.Equal(CallStatus.Ended, endRes.Room!.Status);
        Assert.NotNull(endRes.Room.EndedAt);

        var hist = await svc.GetHistoryAsync(me.Id, 1, 50, default);
        Assert.Equal(1, hist.Total);
    }

    [Fact]
    public async Task Signal_UnknownAction_ReturnsInvalidSignal()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var svc = Svc(t);

        var call = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default)).Room!;
        var res = await svc.SignalAsync(peer.Id, new CallSignalRequest(call.Id, "sleep"), default);

        Assert.False(res.Success);
        Assert.Equal(CallErrors.InvalidSignal, res.ErrorCode);
    }

    [Fact]
    public async Task Update_TogglesMuteAndScreen_WithoutStatusChange()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var svc = Svc(t);

        var call = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default)).Room!;
        var res = await svc.SignalAsync(peer.Id, new CallSignalRequest(call.Id, "update", IsMuted: true, IsScreenSharing: true), default);

        Assert.True(res.Success);
        Assert.Equal(CallStatus.Ringing, res.Room!.Status); // no transition
        var b = res.Room.Participants.Single(p => p.Nik == "B");
        Assert.True(b.IsMuted);
        Assert.True(b.IsScreenSharing);
    }

    [Fact]
    public async Task Signal_NonParticipant_ReturnsNotParticipant()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var intruder = t.AddUser("C");
        var svc = Svc(t);

        var call = (await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default)).Room!;
        var res = await svc.SignalAsync(intruder.Id, new CallSignalRequest(call.Id, "accept"), default);

        Assert.False(res.Success);
        Assert.Equal(CallErrors.NotParticipant, res.ErrorCode);
    }

    [Fact]
    public async Task GetRingingCall_ReturnsInvitedUsersCall_NotCallers()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var peer = t.AddUser("B");
        var other = t.AddUser("C");
        var svc = Svc(t);

        _ = await svc.CreateCallAsync(me.Id, new CreateCallRequest(peer.Nik), default);

        var peerRinging = await svc.GetRingingCallAsync(peer.Id, default);
        Assert.NotNull(peerRinging);

        var callerRinging = await svc.GetRingingCallAsync(me.Id, default); // caller is not "invited"
        Assert.Null(callerRinging);

        var otherRinging = await svc.GetRingingCallAsync(other.Id, default);
        Assert.Null(otherRinging);
    }

    [Fact]
    public void WebRtcSignalDto_SerializesCorrectly()
    {
        var callId = Guid.NewGuid();
        var signal = new WebRtcSignalDto(callId, "offer", "{\"type\":\"offer\",\"sdp\":\"v=0...\"}");

        Assert.Equal(callId, signal.CallId);
        Assert.Equal("offer", signal.Type);
        Assert.Contains("v=0", signal.Payload);
    }
}
