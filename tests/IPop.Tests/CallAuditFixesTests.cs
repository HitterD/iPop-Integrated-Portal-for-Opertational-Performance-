using IPop.Application.Calls;
using IPop.Infrastructure.Calls;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Calls audit 2026-11-03: races between devices/replicas, self-calls, busy (call waiting),
/// the heartbeat + reaper that close calls nobody is in any more, and per-call link quality.
public class CallAuditFixesTests
{
    private static CallService Svc(TestDb t) => new(t.Verify());

    private static async Task<CallRoomDto> RingAsync(TestDb t, Guid caller, string peerNik) =>
        (await Svc(t).CreateCallAsync(caller, new CreateCallRequest(peerNik), default)).Room!;

    [Fact]
    public async Task Calling_yourself_is_refused()
    {
        using var t = new TestDb();
        var me = t.AddUser("A");
        var r = await Svc(t).CreateCallAsync(me.Id, new CreateCallRequest(me.Nik), default);
        Assert.False(r.Success);
        Assert.Equal(CallErrors.SelfCall, r.ErrorCode);
    }

    [Fact]
    public async Task Two_devices_accepting_at_once_only_one_wins()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = await RingAsync(t, a.Id, b.Nik);

        // Both devices loaded the ringing room before either saved (separate contexts = separate requests).
        var phone = Svc(t);
        var laptop = Svc(t);
        var first = phone.SignalAsync(b.Id, new CallSignalRequest(call.Id, "accept"), default);
        var second = laptop.SignalAsync(b.Id, new CallSignalRequest(call.Id, "accept"), default);
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, r => r.Success);
        var loser = Assert.Single(results, r => !r.Success);
        Assert.Contains(loser.ErrorCode, new[] { CallErrors.Conflict, CallErrors.InvalidSignal });
    }

    [Fact]
    public async Task A_late_accept_from_another_device_is_a_conflict_not_a_generic_failure()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = await RingAsync(t, a.Id, b.Nik);
        Assert.True((await Svc(t).SignalAsync(b.Id, new CallSignalRequest(call.Id, "accept"), default)).Success);

        var late = await Svc(t).SignalAsync(b.Id, new CallSignalRequest(call.Id, "accept"), default);
        Assert.Equal(CallErrors.Conflict, late.ErrorCode);
    }

    [Fact]
    public async Task Hang_up_racing_an_accept_never_leaves_an_active_call_with_an_end()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = await RingAsync(t, a.Id, b.Nik);

        // Load both before either saves: the stale writer must lose.
        var callerCtx = t.Verify();
        var calleeCtx = t.Verify();
        var callerRoom = await callerCtx.CallRooms.Include(r => r.Participants).FirstAsync(r => r.Id == call.Id);
        var calleeRoom = await calleeCtx.CallRooms.Include(r => r.Participants).FirstAsync(r => r.Id == call.Id);
        Assert.NotNull(callerRoom); Assert.NotNull(calleeRoom);
        var cancel = await new CallService(callerCtx).SignalAsync(a.Id, new CallSignalRequest(call.Id, "end"), default);
        var accept = await new CallService(calleeCtx).SignalAsync(b.Id, new CallSignalRequest(call.Id, "accept"), default);

        Assert.True(cancel.Success);
        Assert.False(accept.Success);
        var final = await t.Verify().CallRooms.AsNoTracking().FirstAsync(r => r.Id == call.Id);
        Assert.Equal(CallStatus.Missed, final.Status);
        Assert.Null(final.EstablishedAt);
    }

    [Fact]
    public async Task Ring_timeout_and_accept_racing_records_history_once()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = await RingAsync(t, a.Id, b.Nik);

        var acceptCtx = t.Verify();
        _ = await acceptCtx.CallRooms.Include(r => r.Participants).FirstAsync(r => r.Id == call.Id);
        Assert.True((await Svc(t).SignalAsync(b.Id, new CallSignalRequest(call.Id, "accept"), default)).Success);
        // The worker saw "ringing" a moment ago; the room is active now, so nothing may be closed.
        var expired = await Svc(t).ExpireRingingAsync(DateTime.UtcNow.AddMinutes(1), default);
        Assert.Empty(expired);
        Assert.Empty(t.Verify().CallHistories.Where(h => h.CallId == call.Id));
    }

    [Fact]
    public async Task Two_replicas_expiring_the_same_ring_write_one_history_per_person()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = await RingAsync(t, a.Id, b.Nik);
        var cutoff = DateTime.UtcNow.AddMinutes(1);

        var both = await Task.WhenAll(Svc(t).ExpireRingingAsync(cutoff, default), Svc(t).ExpireRingingAsync(cutoff, default));

        Assert.Equal(1, both.Sum(x => x.Count));
        Assert.Equal(2, t.Verify().CallHistories.Count(h => h.CallId == call.Id));
    }

    [Fact]
    public async Task Busy_from_call_waiting_tells_the_caller_and_records_busy()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = await RingAsync(t, a.Id, b.Nik);

        var r = await Svc(t).SignalAsync(b.Id, new CallSignalRequest(call.Id, "busy"), default);

        Assert.True(r.Success);
        Assert.Equal(CallStatus.Declined, r.Room!.Status);
        Assert.Equal("busy", r.Room.EndReason);
        var hist = t.Verify().CallHistories.Where(h => h.CallId == call.Id).ToList();
        Assert.Equal("busy", hist.Single(h => h.PerformerId == a.Id).Result);
        Assert.Equal("declined", hist.Single(h => h.PerformerId == b.Id).Result);
    }

    [Fact]
    public async Task Only_the_invited_person_can_decline_or_send_busy()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = await RingAsync(t, a.Id, b.Nik);

        var callerDecline = await Svc(t).SignalAsync(a.Id, new CallSignalRequest(call.Id, "busy"), default);
        Assert.False(callerDecline.Success);
    }

    [Fact]
    public async Task Heartbeat_only_counts_for_someone_in_a_live_call()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = await RingAsync(t, a.Id, b.Nik);

        Assert.True(await Svc(t).HeartbeatAsync(a.Id, call.Id, null, default));    // caller is in it while ringing
        Assert.False(await Svc(t).HeartbeatAsync(b.Id, call.Id, null, default));   // callee only invited
        await Svc(t).SignalAsync(a.Id, new CallSignalRequest(call.Id, "end"), default);
        Assert.False(await Svc(t).HeartbeatAsync(a.Id, call.Id, null, default));   // over
    }

    [Fact]
    public async Task Reaper_ends_a_call_whose_tab_stopped_beating_and_spares_the_live_ones()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var c = t.AddUser("C");
        var d = t.AddUser("D");
        var stale = await RingAsync(t, a.Id, b.Nik);
        var live = await RingAsync(t, c.Id, d.Nik);
        await Svc(t).SignalAsync(b.Id, new CallSignalRequest(stale.Id, "accept"), default);
        await Svc(t).SignalAsync(d.Id, new CallSignalRequest(live.Id, "accept"), default);
        foreach (var (u, id) in new[] { (a.Id, stale.Id), (b.Id, stale.Id), (c.Id, live.Id), (d.Id, live.Id) })
            await Svc(t).HeartbeatAsync(u, id, null, default);
        // A's laptop went to sleep 3 minutes ago; everyone else is beating.
        await t.Verify().CallParticipants.Where(p => p.CallId == stale.Id && p.UserId == a.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastSeenAt, DateTime.UtcNow.AddMinutes(-3)));

        var reaped = await Svc(t).ReapStaleAsync(DateTime.UtcNow.AddMinutes(-2), default);

        var room = Assert.Single(reaped);
        Assert.Equal(stale.Id, room.Id);
        Assert.Equal(CallStatus.Ended, room.Status);
        Assert.Equal(CallStatus.Active, (await t.Verify().CallRooms.FirstAsync(r => r.Id == live.Id)).Status);
    }

    [Fact]
    public async Task Reaper_leaves_old_clients_that_never_beat_alone()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = await RingAsync(t, a.Id, b.Nik);
        await Svc(t).SignalAsync(b.Id, new CallSignalRequest(call.Id, "accept"), default);

        Assert.Empty(await Svc(t).ReapStaleAsync(DateTime.UtcNow.AddMinutes(5), default));
    }

    [Fact]
    public async Task Disconnect_on_one_replica_keeps_a_call_that_beat_after_the_drop()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = await RingAsync(t, a.Id, b.Nik);
        await Svc(t).SignalAsync(b.Id, new CallSignalRequest(call.Id, "accept"), default);
        await Svc(t).HeartbeatAsync(a.Id, call.Id, null, default);

        // This replica lost A's last connection a minute ago, but A's tab beat since (other replica).
        var ended = await Svc(t).EndForUserAsync(a.Id, default, unlessSeenAfter: DateTime.UtcNow.AddMinutes(-1));
        Assert.Empty(ended);

        // No beat since the drop: the call ends as before.
        var endedNow = await Svc(t).EndForUserAsync(a.Id, default, unlessSeenAfter: DateTime.UtcNow.AddMinutes(1));
        Assert.Single(endedNow);
    }

    [Fact]
    public async Task Room_keeps_the_worst_reported_quality_and_history_shows_it()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var call = await RingAsync(t, a.Id, b.Nik);
        await Svc(t).SignalAsync(b.Id, new CallSignalRequest(call.Id, "accept"), default);

        await Svc(t).HeartbeatAsync(a.Id, call.Id, "poor", default);
        await Svc(t).HeartbeatAsync(b.Id, call.Id, "good", default);   // never improves a recorded "poor"
        await Svc(t).SignalAsync(a.Id, new CallSignalRequest(call.Id, "end"), default);

        Assert.Equal("poor", (await t.Verify().CallRooms.FirstAsync(r => r.Id == call.Id)).Quality);
        var page = await Svc(t).GetHistoryAsync(a.Id, 1, 10, default);
        Assert.Equal("poor", page.Items.Single().Quality);
    }

    [Theory]
    [InlineData(null, "fair", "fair")]
    [InlineData("poor", "fair", "poor")]
    [InlineData("good", "junk", "good")]
    public void Worse_quality_wins(string? current, string? reported, string? expected) =>
        Assert.Equal(expected, CallService.WorseQuality(current, reported));
}
