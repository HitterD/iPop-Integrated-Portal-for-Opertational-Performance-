using IPop.Application.Calls;
using IPop.Application.Rbac;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Calls;

/// <summary>
/// Calls signaling hub. The client invokes the same state machine the REST API exposes
/// (create/signal/get) and receives lifecycle broadcasts on its user group
/// ("user:{id}") — ringing, state-changed, participant-joined/left, ended.
/// Media transport (WebRTC/LiveKit) is intentionally out of scope this wave.
/// </summary>
public class CallHub(ICallService callService, IPopDbContext db, IPermissionEvaluator perms) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        if (userId is null) { Context.Abort(); return; }
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}", Context.ConnectionAborted);
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

        var room = result.Room;
        foreach (var p in room.Participants)
        {
            if (room.Status is CallStatus.Ended or CallStatus.Declined or CallStatus.Missed)
            {
                await Clients.Group($"user:{p.UserId}").SendAsync("callEnded", Context.ConnectionAborted);
            }
            else
            {
                await Clients.Group($"user:{p.UserId}").SendAsync("callStateChanged", room, Context.ConnectionAborted);
            }
        }
    }

    /// WebRTC signaling relay: forwards offer, answer, ice-candidates to the other participant.
    public async Task SendWebRtcSignal(WebRtcSignalDto signal)
    {
        var userId = GetUserId();
        if (userId is null) { Context.Abort(); return; }

        var room = await db.CallRooms.Include(r => r.Participants)
            .FirstOrDefaultAsync(r => r.Id == signal.CallId, Context.ConnectionAborted);
        if (room is null) return;

        // Verify sender is participant in this room
        if (!room.Participants.Any(p => p.UserId == userId.Value)) return;

        // Broadcast to other participants
        var others = room.Participants.Where(p => p.UserId != userId.Value);
        foreach (var p in others)
        {
            await Clients.Group($"user:{p.UserId}").SendAsync("webrtcSignalReceived", signal, Context.ConnectionAborted);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
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
