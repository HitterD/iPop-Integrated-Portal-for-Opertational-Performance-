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

        var room = await db.CallRooms.AsNoTracking().Include(r => r.Participants)
            .FirstOrDefaultAsync(r => r.Id == signal.CallId, Context.ConnectionAborted);
        // Only a live call, and only someone still in it (not left/declined) may signal.
        if (room is null || room.Status is not (CallStatus.Ringing or CallStatus.Active)) return;
        static bool InCall(string status) => status is CallParticipantStatus.Accepted or CallParticipantStatus.Invited;
        if (!room.Participants.Any(p => p.UserId == userId.Value && InCall(p.Status))) return;

        // Broadcast to other participants, stamped with the sending tab: the other user may have
        // several tabs/devices, and the receiver must stick to one peer. The tab id survives a
        // refresh (sessionStorage) so a reloaded peer is still the same peer; the connection id
        // does not, and is only the fallback for clients that send no tab id.
        var tab = signal.Tab is { Length: > 0 and <= 64 } t ? "tab:" + t : Context.ConnectionId;
        signal = signal with { From = tab, Tab = null };
        var others = room.Participants.Where(p => p.UserId != userId.Value && InCall(p.Status));
        foreach (var p in others)
        {
            await Clients.Group($"user:{p.UserId}").SendAsync("webrtcSignalReceived", signal, Context.ConnectionAborted);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("userId", out var id) && id is Guid userId)
            connections.Disconnected(userId);
        await base.OnDisconnectedAsync(exception);
    }

    private Guid? GetUserId()
    {
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
