using IPop.Api.Calls;
using IPop.Api.Rbac;
using IPop.Application.Calls;
using IPop.Application.Realtime;
using IPop.Infrastructure.Notifications.Push;
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
            ICallService svc, IPopDbContext db, IHubContext<CallHub> hub, PushQueue push, CancellationToken ct) =>
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
            await SyncCallToastAsync(push, db, result.Room, ct);

            return Results.Created($"/api/calls/{result.Room.Id}", result.Room);
        }).RequireRateLimiting(CallRateLimitPolicy);

        // Group call for a chat group: create (rings every other member) or join the live one.
        // The response carries the caller's LiveKit token; media never falls back to P2P.
        g.MapPost("/group/{chatRoomId:guid}", async (Guid chatRoomId, HttpContext http,
            ICallService svc, IPopDbContext db, IHubContext<CallHub> hub, ILiveKitTokenService liveKit, PushQueue push, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (!liveKit.IsConfigured)
                return Results.Json(new { code = CallErrors.MediaUnavailable, message = "The group call media server isn't configured." }, statusCode: 503);
            var result = await svc.StartGroupCallAsync(userId.Value, chatRoomId, ct);
            if (result.Room is null) return GroupError(result);
            await BroadcastGroupAsync(hub, db, result.Room, ct, push);
            return Results.Ok(await WithTokenAsync(result.Room, userId.Value, db, liveKit, ct));
        });

        // Join a group call (accept the ring, or late join from the chat banner) → LiveKit token.
        g.MapPost("/{callId:guid}/join", async (Guid callId, HttpContext http,
            ICallService svc, IPopDbContext db, IHubContext<CallHub> hub, ILiveKitTokenService liveKit, PushQueue push, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (!liveKit.IsConfigured)
                return Results.Json(new { code = CallErrors.MediaUnavailable, message = "The group call media server isn't configured." }, statusCode: 503);
            var result = await svc.JoinGroupCallAsync(userId.Value, callId, ct);
            if (result.Room is null) return GroupError(result);
            await BroadcastGroupAsync(hub, db, result.Room, ct, push);
            return Results.Ok(await WithTokenAsync(result.Room, userId.Value, db, liveKit, ct));
        });

        // "Add person": an accepted participant rings a directory user into this call. A 1:1 call
        // becomes an ad-hoc LiveKit call; everyone gets the new state (the P2P pair re-joins via
        // /join for a token) and the invitee gets the ring + a push toast.
        g.MapPost("/{callId:guid}/invite", async (Guid callId, InviteToCallRequest req, HttpContext http,
            ICallService svc, IPopDbContext db, IHubContext<CallHub> hub, ILiveKitTokenService liveKit, PushQueue push, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (!liveKit.IsConfigured)
                return Results.Json(new { code = CallErrors.MediaUnavailable, message = "Adding people needs the call media server, which is not configured." }, statusCode: 503);
            var result = await svc.InviteAsync(userId.Value, callId, req.Nik, ct);
            if (result.Room is null)
                return result.ErrorCode switch
                {
                    CallErrors.AlreadyInCall => Results.Json(new { code = result.ErrorCode, message = result.Error }, statusCode: 409),
                    CallErrors.PeerNotFound => Results.Json(new { code = result.ErrorCode, message = result.Error }, statusCode: 404),
                    _ => GroupError(result),
                };
            await BroadcastGroupAsync(hub, db, result.Room, ct, push);
            return Results.Ok(await WithTokenAsync(result.Room, userId.Value, db, liveKit, ct));
        });

        // The chat group's live call (banner "Panggilan grup sedang berlangsung"), or null.
        g.MapGet("/group/{chatRoomId:guid}/active", async (Guid chatRoomId, HttpContext http,
            ICallService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.GetActiveGroupCallAsync(userId.Value, chatRoomId, ct);
            if (!result.Success) return GroupError(result);
            return Results.Ok(result.Room);
        });

        // Signaling lifecycle: accept / decline / leave / end + mute/screen toggles.
        g.MapPost("/signal", async (CallSignalRequest req, HttpContext http,
            ICallService svc, IPopDbContext db, IHubContext<CallHub> hub, PushQueue push, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.SignalAsync(userId.Value, req, ct);
            if (result.Room is null)
                return Results.Json(new { code = result.ErrorCode, message = result.Error },
                    statusCode: result.ErrorCode == CallErrors.Conflict ? 409 : 422);

            // Ended rooms go out as state too, so each side can say why (declined, missed…).
            // Group calls reach every group member (banners), not only the participants.
            await BroadcastGroupAsync(hub, db, result.Room, ct, push);

            return Results.Ok(result.Room);
        });

        // The tab holding my media is alive (every ~20 s) + its measured link quality. 404 = the call
        // is over (or I am no longer in it): the tab learns that within one beat.
        g.MapPost("/{callId:guid}/heartbeat", async (Guid callId, CallHeartbeatRequest? req, HttpContext http,
            ICallService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            return await svc.HeartbeatAsync(userId.Value, callId, req?.Quality, ct)
                ? Results.NoContent()
                : Results.Json(new { code = CallErrors.RoomNotFound, message = "Not in this call." }, statusCode: 404);
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

        // The live call I am in (answered, or my outgoing ring): a reloaded tab rejoins it.
        g.MapGet("/active", async (HttpContext http,
            ICallService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var room = await svc.GetActiveCallAsync(userId.Value, ct);
            return Results.Ok(room);
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

        // Mark missed calls as seen (all, or one) — clears the sidebar badge on every open tab.
        g.MapPost("/history/seen", (HttpContext http, ICallService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct)
            => MarkSeenAsync(http, null, svc, db, realtime, ct));
        g.MapPost("/history/{callId:guid}/seen", (Guid callId, HttpContext http, ICallService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct)
            => MarkSeenAsync(http, callId, svc, db, realtime, ct));

        // ICE servers (STUN + the LAN TURN) for the browser's RTCPeerConnection.
        g.MapGet("/config", (IConfiguration cfg, HttpContext http) =>
        {
            var iceServers = BuildIceServers(cfg, http.User.Identity?.Name ?? "user", DateTimeOffset.UtcNow);
            return Results.Ok(new { iceServers, ringTimeoutSeconds = (int)IPop.Infrastructure.Calls.CallService.RingTimeout.TotalSeconds });
        });

        return app;
    }

    /// Placing calls: 10 per minute per user (ring spam), see Program.cs.
    public const string CallRateLimitPolicy = "calls-create";

    private static IResult GroupError(CallResultDto result) => result.ErrorCode switch
    {
        CallErrors.Conflict => Results.Json(new { code = result.ErrorCode, message = result.Error }, statusCode: 409),
        CallErrors.GroupNotFound or CallErrors.RoomNotFound =>
            Results.Json(new { code = result.ErrorCode, message = result.Error }, statusCode: 404),
        CallErrors.NotGroupMember or CallErrors.NotParticipant =>
            Results.Json(new { code = result.ErrorCode, message = result.Error }, statusCode: 403),
        _ => Results.Json(new { code = result.ErrorCode, message = result.Error }, statusCode: 422),
    };

    /// Mint the caller's LiveKit token for a group call. Identity = NIK (same as meeting rooms).
    /// LiveKitUrl is the configured server; a browser on https swaps it for wss://{page host}.
    private static async Task<CallRoomDto> WithTokenAsync(CallRoomDto room, Guid userId, IPopDbContext db,
        ILiveKitTokenService liveKit, CancellationToken ct)
    {
        var me = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Nik, u.Name }).FirstAsync(ct);
        var name = IPop.Infrastructure.Calls.CallService.LiveKitRoomName(room.Id);
        return room with
        {
            LiveKitUrl = liveKit.ServerUrl,
            LiveKitToken = liveKit.GenerateToken(name, me.Nik, me.Name, TimeSpan.FromHours(6)),
            LiveKitRoom = name,
        };
    }

    /// callStateChanged to every participant and, for group calls, to every current group member.
    internal static async Task BroadcastGroupAsync(IHubContext<CallHub> hub, IPopDbContext db, CallRoomDto room, CancellationToken ct,
        PushQueue? push = null)
    {
        await SyncCallToastAsync(push, db, room, ct);
        var targets = room.Participants.Select(p => p.UserId).ToHashSet();
        if (room.ChatRoomId is { } chatId)
        {
            var members = await db.ChatRoomMembers.AsNoTracking()
                .Where(m => m.RoomId == chatId && m.LeftAt == null).Select(m => m.UserId).ToListAsync(ct);
            targets.UnionWith(members);
        }
        foreach (var uid in targets)
            await hub.Clients.Group($"user:{uid}").SendAsync("callStateChanged", room, ct);
    }

    /// Ring (or close) the Windows call toast of invited users to match this call state.
    /// The call change is already committed; this durably records "sync this call's toasts", and the
    /// push worker diffs the current call state against earlier rings (ring once, close once, stale rings never sent).
    internal static async Task SyncCallToastAsync(PushQueue? push, IPopDbContext db, CallRoomDto room, CancellationToken ct)
    {
        if (push is null) return;
        try { await push.EnqueueAsync(db, "call", room.Id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fail the call itself over its toast; SignalR already carried the state.
            Console.Error.WriteLine($"Call toast enqueue failed: {ex.GetType().Name}");
        }
    }

    /// How long an ephemeral TURN credential stays valid.
    internal static readonly TimeSpan TurnCredentialLifetime = TimeSpan.FromHours(12);

    /// ICE servers for the browser. Static TURN passwords are never sent (pentest V-02: the
    /// configured one was the LiveKit API secret). A server with "sharedSecret" gets a
    /// short-lived TURN REST credential instead (coturn use-auth-secret: username
    /// "expiry:user", credential base64(HMAC-SHA1(secret, username))); one with only a static
    /// "credential" is reduced to its STUN urls. Group calls get TURN from LiveKit's own join.
    internal static List<IceServerDto> BuildIceServers(IConfiguration cfg, string user, DateTimeOffset now)
    {
        // Read explicitly: binding to object[] yields empty objects, so TURN never reached the browser.
        // Layered files can give "urls" both a scalar (base file) and children (Local array);
        // the array is the more specific one, so it wins.
        var iceServers = cfg.GetSection("Calls:IceServers").GetChildren()
            .Select(s =>
            {
                var urls = s.GetSection("urls");
                var list = urls.GetChildren().Select(u => u.Value).OfType<string>().ToArray();
                if (list.Length == 0 && urls.Value is { } one) list = [one];
                if (s["sharedSecret"] is { Length: > 0 } secret)
                {
                    var username = $"{(now + TurnCredentialLifetime).ToUnixTimeSeconds()}:{user}";
                    using var hmac = new System.Security.Cryptography.HMACSHA1(System.Text.Encoding.UTF8.GetBytes(secret));
                    var credential = Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(username)));
                    return new IceServerDto(list, username, credential);
                }
                return new IceServerDto(list.Where(u => u.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)).ToArray(), null, null);
            })
            .Where(s => s.Urls.Length > 0)
            .ToList();
        if (iceServers.Count == 0) iceServers.Add(new IceServerDto(["stun:stun.l.google.com:19302"], null, null));
        return iceServers;
    }

    public sealed record CallHeartbeatRequest(string? Quality);

    internal sealed record IceServerDto(string[] Urls,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Username,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Credential);

    private static async Task<IResult> MarkSeenAsync(HttpContext http, Guid? callId, ICallService svc,
        IPopDbContext db, IRealtime realtime, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(http, db, ct);
        if (userId is null) return Results.Unauthorized();
        var count = await svc.MarkHistorySeenAsync(userId.Value, callId, ct);
        var nik = http.User.FindFirst("nik")!.Value;
        await realtime.ToUserAsync(nik, RealtimeEvents.BadgeChanged, new RealtimeArgs("calls", count), ct);
        return Results.Ok(new { count });
    }

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        // The access token already carries the user id (sub); only tokens without it fall back to the NIK lookup.
        if (IPop.Api.Auth.CurrentUser.IdFromToken(http.User) is { } fromToken) return fromToken;
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
