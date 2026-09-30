using IPop.Application.Calls;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Calls;

/// <summary>
/// 1:1 calls state machine. Media is peer-to-peer WebRTC (STUN/TURN from /api/calls/config);
/// this service keeps the room/participant/history state consistent, and the hub relays
/// signaling over it. Every action that would be a 500 on a bad payload returns CallResultDto(Success=false).
/// </summary>
public sealed partial class CallService(IPopDbContext db) : ICallService
{
    /// A ringing call nobody answers becomes "missed" after this long (worker + /ringing filter).
    public static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(45);

    public async Task<CallResultDto> CreateCallAsync(Guid callerId, CreateCallRequest req, CancellationToken ct)
    {
        // Peer addressed by NIK (directory identity) — resolved server-side.
        if (string.IsNullOrWhiteSpace(req.PeerNik))
            return Fail(CallErrors.PeerNotFound, "Peer NIK is required.");
        var peer = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == req.PeerNik && u.IsActive, ct);
        if (peer is null)
            return Fail(CallErrors.PeerNotFound, "Peer user not found or inactive.");

        // Stub token — a real gateway issues a short-lived ephemeral token per invite.
        var now = DateTime.UtcNow;
        var room = new CallRoom
        {
            Id = Guid.NewGuid(),
            CreatorId = callerId,
            Kind = "user",
            Status = CallStatus.Ringing,
            Token = Guid.NewGuid().ToString("N"),
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Quality = NormalizeQuality(req.Quality),
        };

        db.CallRooms.Add(room);
        db.CallParticipants.Add(new CallParticipant
        {
            Id = Guid.NewGuid(), CallId = room.Id, UserId = callerId,
            Status = CallParticipantStatus.Accepted, JoinedAt = now,
        });
        db.CallParticipants.Add(new CallParticipant
        {
            Id = Guid.NewGuid(), CallId = room.Id, UserId = peer.Id,
            Status = CallParticipantStatus.Invited, JoinedAt = now,
        });

        await db.SaveChangesAsync(ct);
        return new CallResultDto(true, null, null, await GetRoomCoreAsync(room.Id, callerId, ct));
    }

    public async Task<CallResultDto> SignalAsync(Guid userId, CallSignalRequest req, CancellationToken ct)
    {
        var room = await db.CallRooms.Include(r => r.Participants).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(r => r.Id == req.CallId, ct);
        if (room is null) return Fail(CallErrors.RoomNotFound, "Call not found.");
        if (room.Status is CallStatus.Ended or CallStatus.Declined or CallStatus.Missed)
            return Fail(CallErrors.CallAlreadyEnded, "Call has already ended.");

        if (room.Kind == CallKind.Group)
            return await GroupSignalAsync(room, userId, req, ct);

        var me = room.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is null) return Fail(CallErrors.NotParticipant, "You are not in this call.");

        var now = DateTime.UtcNow;
        switch (req.Action)
        {
            case "accept":
                if (me.Status != CallParticipantStatus.Invited)
                    return Fail(CallErrors.InvalidSignal, "Only the invited participant can accept.");
                me.Status = CallParticipantStatus.Accepted;
                room.Status = CallStatus.Active;
                room.EstablishedAt = now;
                break;

            case "decline":
                if (room.Status != CallStatus.Ringing)
                    return Fail(CallErrors.InvalidSignal, "You can only decline a ringing call.");
                me.Status = CallParticipantStatus.Declined;
                room.Status = CallStatus.Declined;
                room.EndedAt = now;
                RecordHistory(room, now);
                break;

            case "leave":
                // A participant leaves: mark left. The call ends only when NO participant
                // with "accepted" status is left (i.e. the last active one hung up).
                me.Status = CallParticipantStatus.Left;
                me.LeftAt = now;
                room.UpdatedAt = now;
                if (!room.Participants.Any(p => p.Status == CallParticipantStatus.Accepted))
                {
                    EndRoom(room, now);
                }
                break;

            case "end":
                // Any participant hangs up → everyone is done. While still ringing this is
                // the caller cancelling: "canceled" for them, "missed" for the callee.
                EndRoom(room, now);
                break;

            case "update":
                // Participant updates mute or screen sharing flags
                break;

            default:
                return Fail(CallErrors.InvalidSignal, $"Unknown signal \"{req.Action}\".");
        }

        // Stub toggles only affect the requester's own flags (real mute is the app's job).
        if (req.IsMuted is { } muted) me.IsMuted = muted;
        if (req.IsScreenSharing is { } sharing) me.IsScreenSharing = sharing;
        room.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return new CallResultDto(true, null, null, ToRoom(room));
    }

    public async Task<CallRoomDto?> GetRoomAsync(Guid callId, Guid userId, CancellationToken ct)
    {
        var room = await db.CallRooms.AsNoTracking()
            .Include(r => r.ChatRoom)
            .Include(r => r.Participants).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(r => r.Id == callId, ct);
        if (room is null) return null;
        if (!room.Participants.Any(p => p.UserId == userId)) return null; // only participants see state
        return ToRoom(room);
    }

    public async Task<CallRoomDto?> GetRingingCallAsync(Guid userId, CancellationToken ct)
    {
        // A ring older than the timeout is stale (the worker will mark it missed) — never resurrect it.
        var cutoff = DateTime.UtcNow - RingTimeout;
        // Group calls keep ringing invitees for the window even once someone has joined (Active).
        var room = await db.CallRooms.AsNoTracking()
            .Include(r => r.ChatRoom)
            .Include(r => r.Participants).ThenInclude(p => p.User)
            .Where(r => (r.Status == CallStatus.Ringing || (r.Kind == CallKind.Group && r.Status == CallStatus.Active)) && r.StartedAt >= cutoff &&
                        r.Participants.Any(p => p.UserId == userId && p.Status == CallParticipantStatus.Invited))
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        return room is null ? null : ToRoom(room);
    }

    public async Task<CallHistoryPage> GetHistoryAsync(Guid userId, int page, int pageSize, CancellationToken ct)
    {
        var p = Math.Max(1, page);
        var size = Math.Clamp(pageSize, 1, 100);
        var q = db.CallHistories.AsNoTracking().Where(h => h.PerformerId == userId);
        var total = await q.CountAsync(ct);

        // Raw per-page rows first (no nav projection in SQL), then resolve peer names
        // + NIKs from the calls in one follow-up query (NIKs needed for Chat jump).
        var raw = await q
            .OrderByDescending(h => h.StartedAt)
            .Skip((p - 1) * size).Take(size)
            .Select(h => new CallHistoryDto(
                h.CallId, h.Direction, h.Result, h.DurationSeconds, h.StartedAt, h.EndedAt, "?", null))
            .ToListAsync(ct);

        var callIds = raw.Select(i => i.CallId).Distinct().ToList();
        var peers = await db.CallRooms.AsNoTracking()
            .Where(r => callIds.Contains(r.Id))
            .SelectMany(r => r.Participants.Where(pt => pt.UserId != userId),
                (r, pt) => new { r.Id, pt.User.Name, pt.User.Nik })
            .GroupBy(x => x.Id)
            .Select(g => new { g.Key, Name = g.First().Name, Nik = g.First().Nik })
            .ToListAsync(ct);
        var peerMap = peers.ToDictionary(x => x.Key, x => x);

        // Group calls are labelled with the chat group, not with one of the participants.
        var groups = await db.CallRooms.AsNoTracking()
            .Where(r => callIds.Contains(r.Id) && r.ChatRoomId != null)
            .Select(r => new { r.Id, r.ChatRoomId, Name = r.ChatRoom!.Name })
            .ToListAsync(ct);
        var groupMap = groups.ToDictionary(x => x.Id, x => x);

        var items = raw
            .Select(i => groupMap.TryGetValue(i.CallId, out var grp)
                ? new CallHistoryDto(i.CallId, i.Direction, i.Result, i.DurationSeconds,
                    i.StartedAt, i.EndedAt, string.IsNullOrWhiteSpace(grp.Name) ? "Panggilan grup" : grp.Name, null, grp.ChatRoomId)
                : peerMap.TryGetValue(i.CallId, out var peer)
                ? new CallHistoryDto(i.CallId, i.Direction, i.Result, i.DurationSeconds,
                    i.StartedAt, i.EndedAt, peer.Name, peer.Nik)
                : new CallHistoryDto(i.CallId, i.Direction, i.Result, i.DurationSeconds,
                    i.StartedAt, i.EndedAt, "Unknown", null))
            .ToList();

        return new CallHistoryPage(items, total, p, size);
    }

    public async Task<IReadOnlyList<CallRoomDto>> ExpireRingingAsync(DateTime cutoff, CancellationToken ct)
    {
        var rooms = await db.CallRooms.Include(r => r.ChatRoom).Include(r => r.Participants).ThenInclude(p => p.User)
            .Where(r => r.Status == CallStatus.Ringing && r.StartedAt < cutoff)
            .ToListAsync(ct);
        if (rooms.Count == 0) return [];

        var now = DateTime.UtcNow;
        foreach (var room in rooms)
        {
            if (room.Kind == CallKind.Group)
            {
                // Nobody joined the creator within the window: the whole group call is missed.
                EndGroupRoom(room, now, timedOut: true);
                continue;
            }
            room.Status = CallStatus.Missed;
            room.EndedAt = now;
            room.UpdatedAt = now;
            RecordHistory(room, now);
        }
        await db.SaveChangesAsync(ct);
        return rooms.Select(r => ToRoom(r)).ToList();
    }

    public async Task<IReadOnlyList<CallRoomDto>> EndForUserAsync(Guid userId, CancellationToken ct)
    {
        // Every open call the user is live in: active calls, and rings they started.
        // A callee who drops while being rung is left to the timeout (another tab may still answer).
        var rooms = await db.CallRooms.Include(r => r.ChatRoom).Include(r => r.Participants).ThenInclude(p => p.User)
            .Where(r => (r.Status == CallStatus.Active &&
                            r.Participants.Any(p => p.UserId == userId && p.Status == CallParticipantStatus.Accepted))
                     || (r.Status == CallStatus.Ringing && r.CreatorId == userId))
            .ToListAsync(ct);
        if (rooms.Count == 0) return [];

        var now = DateTime.UtcNow;
        foreach (var room in rooms)
        {
            var me = room.Participants.First(p => p.UserId == userId);
            if (room.Kind == CallKind.Group)
            {
                // A dropped member leaves the group call; it ends only when nobody is left in it.
                if (me.Status == CallParticipantStatus.Accepted)
                {
                    me.Status = CallParticipantStatus.Left;
                    me.LeftAt = now;
                }
                room.UpdatedAt = now;
                if (!room.Participants.Any(p => p.Status == CallParticipantStatus.Accepted))
                    EndGroupRoom(room, now, timedOut: false);
                continue;
            }
            me.Status = CallParticipantStatus.Left;
            me.LeftAt = now;
            EndRoom(room, now);
        }
        await db.SaveChangesAsync(ct);
        return rooms.Select(r => ToRoom(r)).ToList();
    }

    // ── internals ──────────────────────────────────────────────────────────────

    private async Task<CallRoomDto?> GetRoomCoreAsync(Guid callId, Guid userId, CancellationToken ct)
    {
        var room = await db.CallRooms.AsNoTracking()
            .Include(r => r.ChatRoom)
            .Include(r => r.Participants).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(r => r.Id == callId, ct);
        return room is null ? null : ToRoom(room);
    }

    /// Ringing → the caller gave up, so it is "missed" for the callee; active → a normal end.
    private void EndRoom(CallRoom room, DateTime now)
    {
        var canceled = room.EstablishedAt is null;
        room.Status = canceled ? CallStatus.Missed : CallStatus.Ended;
        room.EndedAt = now;
        room.UpdatedAt = now;
        RecordHistory(room, now, callerCanceled: canceled);
    }

    /// History results — caller: completed | canceled | declined | no-answer;
    /// callee: completed | missed | declined. Duration counts from pickup, not from ringing.
    private void RecordHistory(CallRoom room, DateTime endedAt, bool callerCanceled = false)
    {
        var established = room.EstablishedAt is not null;
        var duration = room.EstablishedAt is { } est ? Math.Max(0, (int)(endedAt - est).TotalSeconds) : 0;
        var declined = room.Status == CallStatus.Declined;

        db.CallHistories.Add(new CallHistory
        {
            Id = Guid.NewGuid(),
            CallId = room.Id,
            PerformerId = room.CreatorId,
            Direction = "outgoing",
            Result = established ? "completed" : declined ? "declined" : callerCanceled ? "canceled" : "no-answer",
            DurationSeconds = duration,
            StartedAt = room.StartedAt,
            EndedAt = endedAt,
        });
        foreach (var pid in room.Participants.Where(p => p.UserId != room.CreatorId).Select(p => p.UserId))
        {
            db.CallHistories.Add(new CallHistory
            {
                Id = Guid.NewGuid(),
                CallId = room.Id,
                PerformerId = pid,
                Direction = "incoming",
                Result = established ? "completed" : declined ? "declined" : "missed",
                DurationSeconds = duration,
                StartedAt = room.StartedAt,
                EndedAt = endedAt,
            });
        }
    }

    private static CallRoomDto ToRoom(CallRoom r) => new(
        r.Id, r.Kind, r.Status, r.Token, r.CreatorId, r.StartedAt,
        r.EstablishedAt, r.EndedAt, r.Quality,
        r.Participants
            .OrderBy(p => p.JoinedAt)
            .Select(p => new CallParticipantDto(p.UserId, p.User?.Nik ?? "", p.User?.Name ?? "", p.Status,
                p.IsMuted, p.IsScreenSharing, p.LeftAt))
            .ToList(),
        ChatRoomId: r.ChatRoomId,
        Title: r.ChatRoom?.Name,
        AvatarColor: r.ChatRoom?.AvatarColor,
        AvatarUrl: r.ChatRoom?.AvatarUrl);

    private static string? NormalizeQuality(string? q) => q switch
    {
        "good" or "fair" or "poor" => q,
        _ => null,
    };

    private static CallResultDto Fail(string code, string error) => new(false, error, code, null);
}
