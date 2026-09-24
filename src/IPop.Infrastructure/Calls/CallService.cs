using IPop.Application.Calls;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Calls;

/// <summary>
/// Calls service state machine with WebRTC signaling and LiveKit SFU access token generation.
/// Keeps the room/participant/history state consistent, and the hub broadcasts signaling events over it.
/// Every action that would be a 500 on a bad payload returns CallResultDto(Success=false).
/// </summary>
public sealed class CallService(IPopDbContext db, ILiveKitTokenService? liveKit = null) : ICallService
{
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
                await RecordHistoryAsync(room, now, ct);
                break;

            case "leave":
                // A participant leaves: mark left. The call ends only when NO participant
                // with "accepted" status is left (i.e. the last active one hung up).
                me.Status = CallParticipantStatus.Left;
                me.LeftAt = now;
                room.UpdatedAt = now;
                if (!room.Participants.Any(p => p.Status == CallParticipantStatus.Accepted))
                {
                    room.Status = CallStatus.Ended;
                    room.EndedAt = now;
                    await RecordHistoryAsync(room, now, ct);
                }
                break;

            case "end":
                // Initiator (or any participant) hangs up → everyone is done.
                room.Status = CallStatus.Ended;
                room.EndedAt = now;
                await RecordHistoryAsync(room, now, ct);
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
            .Include(r => r.Participants).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(r => r.Id == callId, ct);
        if (room is null) return null;
        if (!room.Participants.Any(p => p.UserId == userId)) return null; // only participants see state
        return ToRoom(room, userId);
    }

    public async Task<CallRoomDto?> GetRingingCallAsync(Guid userId, CancellationToken ct)
    {
        var room = await db.CallRooms.AsNoTracking()
            .Include(r => r.Participants).ThenInclude(p => p.User)
            .Where(r => r.Status == CallStatus.Ringing &&
                        r.Participants.Any(p => p.UserId == userId && p.Status == CallParticipantStatus.Invited))
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        return room is null ? null : ToRoom(room, userId);
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

        var items = raw
            .Select(i => peerMap.TryGetValue(i.CallId, out var peer)
                ? new CallHistoryDto(i.CallId, i.Direction, i.Result, i.DurationSeconds,
                    i.StartedAt, i.EndedAt, peer.Name, peer.Nik)
                : new CallHistoryDto(i.CallId, i.Direction, i.Result, i.DurationSeconds,
                    i.StartedAt, i.EndedAt, "Unknown", null))
            .ToList();

        return new CallHistoryPage(items, total, p, size);
    }

    // ── internals ──────────────────────────────────────────────────────────────

    private async Task<CallRoomDto?> GetRoomCoreAsync(Guid callId, Guid userId, CancellationToken ct)
    {
        var room = await db.CallRooms.AsNoTracking()
            .Include(r => r.Participants).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(r => r.Id == callId, ct);
        return room is null ? null : ToRoom(room, userId);
    }

    private async Task RecordHistoryAsync(CallRoom room, DateTime endedAt, CancellationToken ct)
    {
        var duration = room.EstablishedAt is null ? 0
            : Math.Max(0, (int)(endedAt - room.EstablishedAt.Value).TotalSeconds);

        // Performer = initiator. Direction outgoing for initiator; each peer gets an incoming row.
        var incomingPeerIds = room.Participants
            .Where(p => p.UserId != room.CreatorId)
            .Select(p => p.UserId)
            .ToList();

        db.CallHistories.Add(new CallHistory
        {
            Id = Guid.NewGuid(),
            CallId = room.Id,
            PerformerId = room.CreatorId,
            Direction = "outgoing",
            Result = room.Status == CallStatus.Declined ? "declined"
                : room.Status == CallStatus.Missed ? "missed"
                : room.EstablishedAt is null ? "canceled" : "completed",
            DurationSeconds = duration,
            StartedAt = room.StartedAt,
            EndedAt = endedAt,
        });
        foreach (var pid in incomingPeerIds)
        {
            db.CallHistories.Add(new CallHistory
            {
                Id = Guid.NewGuid(),
                CallId = room.Id,
                PerformerId = pid,
                Direction = "incoming",
                Result = room.Status == CallStatus.Declined ? "missed"
                    : room.EstablishedAt is null ? "canceled" : "completed",
                DurationSeconds = duration,
                StartedAt = room.StartedAt,
                EndedAt = endedAt,
            });
        }
    }

    private CallRoomDto ToRoom(CallRoom r, Guid? forUserId = null)
    {
        string? lkToken = null;
        if (liveKit is not null && forUserId.HasValue)
        {
            var p = r.Participants.FirstOrDefault(x => x.UserId == forUserId.Value);
            var nik = p?.User?.Nik ?? forUserId.Value.ToString();
            var name = p?.User?.Name ?? nik;
            lkToken = liveKit.GenerateToken(r.Id.ToString(), nik, name);
        }

        return new CallRoomDto(
            r.Id, r.Kind, r.Status, r.Token, r.CreatorId, r.StartedAt,
            r.EstablishedAt, r.EndedAt, r.Quality,
            r.Participants
                .OrderBy(p => p.JoinedAt)
                .Select(p => new CallParticipantDto(p.UserId, p.User?.Nik ?? "", p.User?.Name ?? "", p.Status,
                    p.IsMuted, p.IsScreenSharing, p.LeftAt))
                .ToList(),
            liveKit?.ServerUrl,
            lkToken);
    }

    private static string? NormalizeQuality(string? q) => q switch
    {
        "good" or "fair" or "poor" => q,
        _ => null,
    };

    private static CallResultDto Fail(string code, string error) => new(false, error, code, null);
}
