using IPop.Api.Calls;
using IPop.Api.Rbac;
using IPop.Application.Calls;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// <summary>
/// Calls REST surface (signaling stub). The transport is mocked — this is the state the
/// UI drives. WebRTC/real media is out of scope this wave; audio/visual stays "coming".
/// Requires the "calls.use" permission; the hub (<see cref="IPop.Api.Calls.CallHub"/>)
/// broadcasts the same lifecycle events.
/// </summary>
public static class CallEndpoints
{
    public static IEndpointRouteBuilder MapCalls(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/calls").RequireAuthorization().RequirePermission("calls.use");

        // Start a call (invite a peer). Returns the ringing room.
        g.MapPost("", async (CreateCallRequest req, HttpContext http,
            ICallService svc, IPopDbContext db, IHubContext<CallHub> hub, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.CreateCallAsync(userId.Value, req, ct);
            if (result.Room is null)
                return Results.Json(new { code = result.ErrorCode, message = result.Error }, statusCode: 422);

            // Real-time: notify both participants' hub groups so a connected peer
            // sees the incoming call card without reloading (SignalR only reacts on
            // Signal() today — create must broadcast too).
            foreach (var p in result.Room.Participants)
                await hub.Clients.Group($"user:{p.UserId}").SendAsync("callStateChanged", result.Room, ct);

            return Results.Created($"/api/calls/{result.Room.Id}", result.Room);
        });

        // Signaling lifecycle: accept / decline / leave / end + mute/screen toggles.
        g.MapPost("/signal", async (CallSignalRequest req, HttpContext http,
            ICallService svc, IPopDbContext db, IHubContext<CallHub> hub, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.SignalAsync(userId.Value, req, ct);
            if (result.Room is null)
                return Results.Json(new { code = result.ErrorCode, message = result.Error }, statusCode: 422);

            foreach (var p in result.Room.Participants)
            {
                if (result.Room.Status is CallStatus.Ended or CallStatus.Declined or CallStatus.Missed)
                {
                    await hub.Clients.Group($"user:{p.UserId}").SendAsync("callEnded", ct);
                }
                else
                {
                    await hub.Clients.Group($"user:{p.UserId}").SendAsync("callStateChanged", result.Room, ct);
                }
            }

            return Results.Ok(result.Room);
        });

        // Live room state (active call bar / participant grid).
        g.MapGet("/{callId:guid}", async (Guid callId, HttpContext http,
            ICallService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var room = await svc.GetRoomAsync(callId, userId.Value, ct);
            return room is null
                ? Results.Json(new { code = CallErrors.RoomNotFound, message = "Call not found." }, statusCode: 404)
                : Results.Ok(room);
        });

        // Ringing (incoming) call for the current user — drives the incoming card.
        g.MapGet("/ringing", async (HttpContext http,
            ICallService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var room = await svc.GetRingingCallAsync(userId.Value, ct);
            return room is null ? Results.Ok((CallRoomDto?)null) : Results.Ok(room);
        });

        // Call history (grouped by date on the UI).
        g.MapGet("/history", async (int? page, int? pageSize, HttpContext http,
            ICallService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.GetHistoryAsync(userId.Value, Math.Max(1, page ?? 1),
                Math.Clamp(pageSize ?? 50, 1, 100), ct);
            return Results.Ok(result);
        });

        // Config endpoint returning ICE/STUN or LiveKit URL
        g.MapGet("/config", (IConfiguration cfg) =>
        {
            var iceServers = cfg.GetSection("Calls:IceServers").Get<object[]>() ?? [
                new { urls = "stun:stun.l.google.com:19302" },
                new { urls = "stun:stun1.l.google.com:19302" },
                new { urls = "stun:stun2.l.google.com:19302" }
            ];
            var liveKitUrl = cfg["Calls:LiveKit:Url"];
            return Results.Ok(new { iceServers, liveKitUrl });
        });

        return app;
    }

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
