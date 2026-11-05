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
        if (peer.Id == callerId)
            return Fail(CallErrors.SelfCall, "You can't call yourself.");

        // Both pressed Call at the same moment ("glare"): the peer is already ringing ME. Instead of a second room -
        // where each side saw the other's call as call waiting and both heard ringback until the 45 s timeout - answer
        // the peer's call: it connects at once for both.
        var ringingSince = DateTime.UtcNow - RingTimeout;   // computed here: the subtraction is not translatable
        var crossing = await db.CallRooms
            .Where(r => r.Kind == "user" && r.Status == CallStatus.Ringing && r.CreatorId == peer.Id
                && r.Participants.Any(p => p.UserId == callerId && p.Status == CallParticipantStatus.Invited)
                && r.StartedAt > ringingSince)
            .OrderByDescending(r => r.StartedAt).Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (crossing != Guid.Empty)
        {
            var answered = await SignalAsync(callerId, new CallSignalRequest(crossing, "accept"), ct);
            if (answered.Room is not null) return answered;   // fell through only if it ended or was taken meanwhile
        }

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

        // Group and ad-hoc calls share the LiveKit flow (per-member decline/leave).
        if (CallKind.IsLiveKit(room.Kind))
            return await GroupSignalAsync(room, userId, req, ct);

        var me = room.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is null) return Fail(CallErrors.NotParticipant, "You are not in this call.");

        var now = DateTime.UtcNow;
        switch (req.Action)
        {
            case "accept":
                // Answered on another of my devices a moment ago: this one must stand down.
                if (me.Status == CallParticipantStatus.Accepted && room.CreatorId != userId)
                    return Fail(CallErrors.Conflict, "The call was answered on another device.");
                if (me.Status != CallParticipantStatus.Invited || room.Status != CallStatus.Ringing)
                    return Fail(CallErrors.InvalidSignal, "Only the invited participant can accept.");
                me.Status = CallParticipantStatus.Accepted;
                room.Status = CallStatus.Active;
                room.EstablishedAt = now;
                break;

            case "decline":
            case "busy":
                // "busy": turned down from the call-waiting banner while on another call.
                if (room.Status != CallStatus.Ringing || me.Status != CallParticipantStatus.Invited)
                    return Fail(CallErrors.InvalidSignal, "You can only decline a ringing call.");
                me.Status = CallParticipantStatus.Declined;
                room.Status = CallStatus.Declined;
                room.EndedAt = now;
                if (req.Action == "busy") room.EndReason = "busy";
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
        room.Quality = WorseQuality(room.Quality, req.Quality);
        room.UpdatedAt = now;

        if (!await TrySaveAsync(room, ct))
            return Fail(CallErrors.Conflict, "The call changed at the same moment. Try again.");
        return new CallResultDto(true, null, null, ToRoom(room));
    }

    /// Saves a call transition only if nobody changed the room since it was read (Version is a
    /// concurrency token): of two racing accepts, or a hang-up and an accept, exactly one wins. It also
    /// keeps several API replicas' timeout workers from closing one call twice (duplicate history).
    private async Task<bool> TrySaveAsync(CallRoom room, CancellationToken ct)
    {
        room.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// The room keeps the worst link quality anyone reported (good, then fair, then poor).
    internal static string? WorseQuality(string? current, string? reported)
    {
        static int Rank(string? q) => q switch { "poor" => 3, "fair" => 2, "good" => 1, _ => 0 };
        var r = NormalizeQuality(reported);
        return Rank(r) > Rank(current) ? r : current;
    }

    public async Task<bool> HeartbeatAsync(Guid userId, Guid callId, string? quality, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Plain column updates: a heartbeat never competes with (or bumps) a state transition.
        var n = await db.CallParticipants
            .Where(p => p.CallId == callId && p.UserId == userId && p.Status == CallParticipantStatus.Accepted
                && (p.Call.Status == CallStatus.Ringing || p.Call.Status == CallStatus.Active))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastSeenAt, now), ct);
        if (n == 0) return false;
        var q = NormalizeQuality(quality);
        if (q == "fair")
            await db.CallRooms.Where(r => r.Id == callId && (r.Quality == null || r.Quality == "good"))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Quality, q), ct);
        else if (q == "poor")
            await db.CallRooms.Where(r => r.Id == callId && (r.Quality == null || r.Quality != "poor"))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Quality, q), ct);
        return true;
    }

    public async Task<IReadOnlyList<CallRoomDto>> ReapStaleAsync(DateTime cutoff, CancellationToken ct)
    {
        // Someone in a live call whose media tab stopped beating (laptop asleep, tab killed, a pod
        // restart lost the disconnect timer). Null = an old client that never beats: left alone.
        var stale = await db.CallParticipants.AsNoTracking()
            .Where(p => p.Status == CallParticipantStatus.Accepted && p.LastSeenAt != null && p.LastSeenAt < cutoff
                && p.Call.Status == CallStatus.Active)
            .Select(p => new { p.CallId, p.UserId })
            .ToListAsync(ct);
        var closed = new List<CallRoomDto>();
        foreach (var s in stale)
        {
            var room = await LoadTrackedAsync(s.CallId, ct);
            if (room is null || room.Status != CallStatus.Active) continue;
            var me = room.Participants.FirstOrDefault(p => p.UserId == s.UserId && p.Status == CallParticipantStatus.Accepted
                && p.LastSeenAt < cutoff);
            if (me is null) continue;
            var now = DateTime.UtcNow;
            me.Status = CallParticipantStatus.Left;
            me.LeftAt = now;
            room.UpdatedAt = now;
            if (CallKind.IsLiveKit(room.Kind))
            {
                if (!room.Participants.Any(p => p.Status == CallParticipantStatus.Accepted))
                    EndGroupRoom(room, now, timedOut: false);
            }
            else EndRoom(room, now);
            if (await TrySaveAsync(room, ct)) closed.Add(ToRoom(room));
        }
        return closed;
    }

    private Task<CallRoom?> LoadTrackedAsync(Guid callId, CancellationToken ct) =>
        db.CallRooms.Include(r => r.ChatRoom).Include(r => r.Participants).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(r => r.Id == callId, ct);

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
        // Group/ad-hoc calls keep ringing invitees for the window even once someone has joined (Active);
        // a person added later ("Add person") rings from their own invite time (JoinedAt), in full.
        var room = await db.CallRooms.AsNoTracking()
            .Include(r => r.ChatRoom)
            .Include(r => r.Participants).ThenInclude(p => p.User)
            .Where(r => (r.Kind == CallKind.User && r.Status == CallStatus.Ringing && r.StartedAt >= cutoff &&
                            r.Participants.Any(p => p.UserId == userId && p.Status == CallParticipantStatus.Invited))
                     || (r.Kind != CallKind.User && (r.Status == CallStatus.Ringing || r.Status == CallStatus.Active) &&
                            r.Participants.Any(p => p.UserId == userId && p.Status == CallParticipantStatus.Invited
                                && (p.InvitedById == null ? r.StartedAt : p.JoinedAt) >= cutoff)))
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        return room is null ? null : ToRoom(room);
    }

    public async Task<CallRoomDto?> GetActiveCallAsync(Guid userId, CancellationToken ct)
    {
        var room = await db.CallRooms.AsNoTracking()
            .Include(r => r.ChatRoom)
            .Include(r => r.Participants).ThenInclude(p => p.User)
            .Where(r => (r.Status == CallStatus.Ringing || r.Status == CallStatus.Active)
                     && r.Participants.Any(p => p.UserId == userId && p.Status == CallParticipantStatus.Accepted))
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
                h.CallId, h.Direction, h.Result, h.DurationSeconds, h.StartedAt, h.EndedAt, "?", null, null,
                h.Direction != "incoming" || h.Result != "missed" || h.SeenAt != null,
                h.Result == "completed" && (h.Call.Quality == "fair" || h.Call.Quality == "poor") ? h.Call.Quality : null))
            .ToListAsync(ct);

        var callIds = raw.Select(i => i.CallId).Distinct().ToList();
        var others = await db.CallRooms.AsNoTracking()
            .Where(r => callIds.Contains(r.Id))
            .SelectMany(r => r.Participants.Where(pt => pt.UserId != userId),
                (r, pt) => new { r.Id, r.Kind, IsCreator = pt.UserId == r.CreatorId, pt.JoinedAt, pt.User.Name, pt.User.Nik })
            .ToListAsync(ct);
        var peerMap = others.GroupBy(x => x.Id).ToDictionary(g => g.Key, g =>
        {
            var ordered = g.OrderByDescending(x => x.IsCreator).ThenBy(x => x.JoinedAt).ToList();
            // Ad-hoc calls (a person was added): "Call with A, B, +N", no single peer to call back.
            return g.First().Kind == CallKind.AdHoc
                ? (Name: AdHocLabel(ordered.Select(x => x.Name).ToList()), Nik: (string?)null)
                : (Name: ordered[0].Name, Nik: (string?)ordered[0].Nik);
        });

        // Group calls are labelled with the chat group, not with one of the participants.
        var groups = await db.CallRooms.AsNoTracking()
            .Where(r => callIds.Contains(r.Id) && r.ChatRoomId != null)
            .Select(r => new { r.Id, r.ChatRoomId, Name = r.ChatRoom!.Name })
            .ToListAsync(ct);
        var groupMap = groups.ToDictionary(x => x.Id, x => x);

        var items = raw
            .Select(i => groupMap.TryGetValue(i.CallId, out var grp)
                ? i with { PeerName = string.IsNullOrWhiteSpace(grp.Name) ? "Group call" : grp.Name, ChatRoomId = grp.ChatRoomId }
                : peerMap.TryGetValue(i.CallId, out var peer)
                ? i with { PeerName = peer.Name, PeerNik = peer.Nik }
                : i with { PeerName = "Unknown" })
            .ToList();

        return new CallHistoryPage(items, total, p, size);
    }

    public async Task<int> MarkHistorySeenAsync(Guid userId, Guid? callId, CancellationToken ct)
    {
        var unseen = db.CallHistories.Where(h => h.PerformerId == userId && h.Direction == "incoming"
            && h.Result == "missed" && h.SeenAt == null);
        if (callId is { } id) unseen = unseen.Where(h => h.CallId == id);
        var rows = await unseen.ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var h in rows) h.SeenAt = now;
        if (rows.Count > 0) await db.SaveChangesAsync(ct);

        // Same window as CallsBadgeProvider.
        var cutoff = now.AddDays(-7);
        return await db.CallHistories.AsNoTracking().CountAsync(h => h.PerformerId == userId
            && h.Direction == "incoming" && h.Result == "missed" && h.SeenAt == null && h.StartedAt >= cutoff, ct);
    }

    public async Task<IReadOnlyList<CallRoomDto>> ExpireRingingAsync(DateTime cutoff, CancellationToken ct)
    {
        var ids = await db.CallRooms.AsNoTracking()
            .Where(r => r.Status == CallStatus.Ringing && r.StartedAt < cutoff)
            .Select(r => r.Id).ToListAsync(ct);
        var closed = new List<CallRoomDto>();
        // One room per save: a room answered (or expired by another replica) meanwhile fails its
        // concurrency check and is skipped, without undoing the others.
        foreach (var id in ids)
        {
            var room = await LoadTrackedAsync(id, ct);
            if (room is null || room.Status != CallStatus.Ringing) continue;
            // A person added to a still-ringing group call gets their full ring before the call gives up.
            if (CallKind.IsLiveKit(room.Kind) && room.Participants.Any(p =>
                    p.Status == CallParticipantStatus.Invited && p.InvitedById != null && p.JoinedAt >= cutoff))
                continue;
            var now = DateTime.UtcNow;
            if (CallKind.IsLiveKit(room.Kind))
            {
                // Nobody joined the creator within the window: the whole group call is missed.
                EndGroupRoom(room, now, timedOut: true);
            }
            else
            {
                room.Status = CallStatus.Missed;
                room.EndedAt = now;
                room.UpdatedAt = now;
                RecordHistory(room, now);
            }
            if (await TrySaveAsync(room, ct)) closed.Add(ToRoom(room));
        }
        return closed;
    }

    public async Task<IReadOnlyList<CallRoomDto>> EndForUserAsync(Guid userId, CancellationToken ct, DateTime? unlessSeenAfter = null)
    {
        // Every open call the user is live in: active calls, and rings they started.
        // A callee who drops while being rung is left to the timeout (another tab may still answer).
        // unlessSeenAfter: this replica lost the user's last connection, but a heartbeat since then
        // means the call's tab lives on (another replica, or it reconnected): keep that call.
        var ids = await db.CallRooms.AsNoTracking()
            .Where(r => (r.Status == CallStatus.Active &&
                            r.Participants.Any(p => p.UserId == userId && p.Status == CallParticipantStatus.Accepted
                                && (unlessSeenAfter == null || p.LastSeenAt == null || p.LastSeenAt < unlessSeenAfter)))
                     || (r.Status == CallStatus.Ringing && r.CreatorId == userId
                            && (unlessSeenAfter == null || !r.Participants.Any(p => p.UserId == userId && p.LastSeenAt >= unlessSeenAfter))))
            .Select(r => r.Id).ToListAsync(ct);
        var closed = new List<CallRoomDto>();
        foreach (var id in ids)
        {
            var room = await LoadTrackedAsync(id, ct);
            if (room is null || room.Status is not (CallStatus.Active or CallStatus.Ringing)) continue;
            var now = DateTime.UtcNow;
            var me = room.Participants.First(p => p.UserId == userId);
            if (CallKind.IsLiveKit(room.Kind))
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
            }
            else
            {
                me.Status = CallParticipantStatus.Left;
                me.LeftAt = now;
                EndRoom(room, now);
            }
            if (await TrySaveAsync(room, ct)) closed.Add(ToRoom(room));
        }
        return closed;
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
        var busy = declined && room.EndReason == "busy";

        db.CallHistories.Add(new CallHistory
        {
            Id = Guid.NewGuid(),
            CallId = room.Id,
            PerformerId = room.CreatorId,
            Direction = "outgoing",
            Result = established ? "completed" : busy ? "busy" : declined ? "declined" : callerCanceled ? "canceled" : "no-answer",
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
                p.IsMuted, p.IsScreenSharing, p.LeftAt, p.JoinedAt, p.InvitedById))
            .ToList(),
        ChatRoomId: r.ChatRoomId,
        Title: r.ChatRoom?.Name,
        AvatarColor: r.ChatRoom?.AvatarColor,
        AvatarUrl: r.ChatRoom?.AvatarUrl,
        EndReason: r.EndReason);

    /// "Call with Jason, Beny, +2" — first names of the other participants, two at most.
    internal static string AdHocLabel(IReadOnlyList<string> names)
    {
        static string First(string n) => n.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? n;
        if (names.Count == 0) return "Call";
        var shown = string.Join(", ", names.Take(2).Select(First));
        return names.Count > 2 ? $"Call with {shown}, +{names.Count - 2}" : $"Call with {shown}";
    }

    internal static string? NormalizeQuality(string? q) => q switch
    {
        "good" or "fair" or "poor" => q,
        _ => null,
    };

    private static CallResultDto Fail(string code, string error) => new(false, error, code, null);
}
