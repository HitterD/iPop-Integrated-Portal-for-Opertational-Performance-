using IPop.Application.Calls;
using IPop.Application.Rbac;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Calls;

/// <summary>
/// Calls signaling hub. Drives the call state machine and relays WebRTC offer/answer/ICE.
/// The client invokes the same state machine the REST API exposes
/// (create/signal/get) and receives lifecycle broadcasts on its user group
/// ("user:{id}") — ringing, state-changed, participant-joined/left, ended.
/// </summary>
public class CallHub(ICallService callService, IPopDbContext db, IPermissionEvaluator perms, CallConnections connections, IHubContext<CallHub> hub,
    IPop.Infrastructure.Notifications.Push.PushQueue push) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        if (userId is null) { Context.Abort(); return; }
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}", Context.ConnectionAborted);
        Context.Items["userId"] = userId.Value;
        connections.Connected(userId.Value);
        await base.OnConnectedAsync();
    }

    /// Signal: accept/decline/leave/end (+ mute/screen toggles) — same as REST /signal.
    public async Task Signal(CallSignalRequest req)
    {
        var userId = GetUserId();
        if (userId is null) { Context.Abort(); return; }
        if (!await perms.IsAllowedAsync(userId.Value, "calls.use", null, Context.ConnectionAborted)) return;

        var result = await callService.SignalAsync(userId.Value, req, Context.ConnectionAborted);
        if (!result.Success || result.Room is null) return; // 422 handled over REST; hub is best-effort signaling

        // Group calls reach every group member (chat banners), 1:1 only the two participants.
        await IPop.Api.Endpoints.CallEndpoints.BroadcastGroupAsync(hub, db, result.Room, Context.ConnectionAborted, push);
    }

    /// WebRTC signaling relay: forwards offer, answer, ice-candidates to the other participant.
    public async Task SendWebRtcSignal(WebRtcSignalDto signal)
    {
        var userId = GetUserId();
        if (userId is null) { Context.Abort(); return; }

        if (!await perms.IsAllowedAsync(userId.Value, "calls.use", null, Context.ConnectionAborted)) return;

        if (signal.Payload is { Length: > 64_000 }) return;   // an SDP is a few KB; never relay junk
        // Only a live call, and only someone still in it (not left/declined) may signal. One small
        // projection (no entity graph) since this runs for every ICE candidate.
        var live = await db.CallParticipants.AsNoTracking()
            .Where(p => p.CallId == signal.CallId
                && (p.Call.Status == CallStatus.Ringing || p.Call.Status == CallStatus.Active)
                && (p.Status == CallParticipantStatus.Accepted || p.Status == CallParticipantStatus.Invited))
            .Select(p => p.UserId)
            .ToListAsync(Context.ConnectionAborted);
        if (!live.Contains(userId.Value)) return;

        // Broadcast to other participants, stamped with the sending tab: the other user may have
        // several tabs/devices, and the receiver must stick to one peer. The tab id survives a
        // refresh (sessionStorage) so a reloaded peer is still the same peer; the connection id
        // does not, and is only the fallback for clients that send no tab id.
        var tab = signal.Tab is { Length: > 0 and <= 64 } t ? "tab:" + t : Context.ConnectionId;
        signal = signal with { From = tab, Tab = null };
        foreach (var other in live.Where(id => id != userId.Value))
            await Clients.Group($"user:{other}").SendAsync("webrtcSignalReceived", signal, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("userId", out var id) && id is Guid userId)
            connections.Disconnected(userId);
        await base.OnDisconnectedAsync(exception);
    }

    /// Resolved once per connection (OnConnectedAsync), then read from Context.Items: no DB round trip
    /// (and never a blocking one) per signal or ICE candidate.
    private Guid? GetUserId()
    {
        if (Context.Items.TryGetValue("userId", out var cached) && cached is Guid known) return known;
        var sub = Context.User?.FindFirst("sub")?.Value
            ?? Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(sub, out var id)) return id;
        var nik = Context.User?.FindFirst("nik")?.Value;
        if (!string.IsNullOrEmpty(nik))
        {
            return db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefault();
        }
        return null;
    }
}
