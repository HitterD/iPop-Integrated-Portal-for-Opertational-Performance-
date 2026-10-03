using IPop.Application.Calls;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Calls;

/// <summary>
/// Group calls (Kind "group"): one ringing/active call per chat group, media over the LiveKit SFU.
/// Participants are the group's members: the starter is accepted, everyone else invited.
/// Unlike 1:1 calls a decline or a hang-up only affects that member; the call ends when the
/// last connected participant leaves, or as "missed" when nobody joins the starter within
/// <see cref="RingTimeout"/>. Invitees who never joined get "missed" in their history.
/// </summary>
public sealed partial class CallService
{
    /// LiveKit room name for a call — kept here so the endpoint and tests agree.
    public static string LiveKitRoomName(Guid callId) => $"call-{callId:N}";

    public async Task<CallResultDto> StartGroupCallAsync(Guid callerId, Guid chatRoomId, CancellationToken ct)
    {
        var chat = await db.ChatRooms.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == chatRoomId && r.Type == "group" && r.IsActive, ct);
        if (chat is null) return Fail(CallErrors.GroupNotFound, "Group not found.");

        var memberIds = await db.ChatRoomMembers.AsNoTracking()
            .Where(m => m.RoomId == chatRoomId && m.LeftAt == null && m.User.IsActive)
            .Select(m => m.UserId)
            .ToListAsync(ct);
        if (!memberIds.Contains(callerId))
            return Fail(CallErrors.NotGroupMember, "You are not a member of this group.");

        // One call per group: starting while one is live joins it instead.
        var existing = await LiveGroupCallIdAsync(chatRoomId, ct);
        if (existing is { } liveId) return await JoinGroupCallAsync(callerId, liveId, ct);

        var now = DateTime.UtcNow;
        var room = new CallRoom
        {
            Id = Guid.NewGuid(),
            CreatorId = callerId,
            Kind = CallKind.Group,
            ChatRoomId = chatRoomId,
            Status = CallStatus.Ringing,
            Token = Guid.NewGuid().ToString("N"),
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CallRooms.Add(room);
        foreach (var uid in memberIds)
        {
            db.CallParticipants.Add(new CallParticipant
            {
                Id = Guid.NewGuid(), CallId = room.Id, UserId = uid,
                Status = uid == callerId ? CallParticipantStatus.Accepted : CallParticipantStatus.Invited,
                // Invitees keep the ring time until they join; ordering puts the starter first.
                JoinedAt = uid == callerId ? now : now.AddTicks(1),
            });
        }
        await db.SaveChangesAsync(ct);
        return new CallResultDto(true, null, null, await GetRoomCoreAsync(room.Id, callerId, ct));
    }

    public async Task<CallResultDto> JoinGroupCallAsync(Guid userId, Guid callId, CancellationToken ct)
    {
        var room = await db.CallRooms.Include(r => r.ChatRoom).Include(r => r.Participants).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(r => r.Id == callId, ct);
        if (room is null || !CallKind.IsLiveKit(room.Kind)) return Fail(CallErrors.RoomNotFound, "Call not found.");
        if (room.Status is not (CallStatus.Ringing or CallStatus.Active))
            return Fail(CallErrors.CallAlreadyEnded, "Call has already ended.");

        var now = DateTime.UtcNow;
        var me = room.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is null)
        {
            // Joined the group after the call started: a current member may still come in.
            // Anyone else needs a participant row (added via "Add person"); ad-hoc calls have no group.
            var isMember = room.ChatRoomId is not null && await db.ChatRoomMembers.AnyAsync(
                m => m.RoomId == room.ChatRoomId && m.UserId == userId && m.LeftAt == null, ct);
            if (!isMember) return Fail(CallErrors.NotParticipant, "You are not in this call.");
            me = new CallParticipant
            {
                Id = Guid.NewGuid(), CallId = room.Id, UserId = userId,
                Status = CallParticipantStatus.Accepted, JoinedAt = now,
            };
            db.CallParticipants.Add(me);
            room.Participants.Add(me);
            me.User = await db.Users.FirstAsync(u => u.Id == userId, ct);
        }
        else if (me.Status != CallParticipantStatus.Accepted)
        {
            // A group call's row was created from membership at ring time: someone removed from
            // (or who left) the group since then may not come back in, unless "Add person" asked them.
            if (room.Kind == CallKind.Group && room.ChatRoomId is not null && me.InvitedById is null
                && !await db.ChatRoomMembers.AnyAsync(m => m.RoomId == room.ChatRoomId && m.UserId == userId && m.LeftAt == null, ct))
                return Fail(CallErrors.NotParticipant, "You are not in this call.");
            // Invited, declined earlier or left: (re)join.
            me.Status = CallParticipantStatus.Accepted;
            me.JoinedAt = now;
            me.LeftAt = null;
            me.IsMuted = false;
        }

        // The call is "established" once a second person is in it.
        if (room.Status == CallStatus.Ringing && room.Participants.Count(p => p.Status == CallParticipantStatus.Accepted) >= 2)
        {
            room.Status = CallStatus.Active;
            room.EstablishedAt = now;
        }
        room.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new CallResultDto(true, null, null, ToRoom(room));
    }

    public async Task<CallResultDto> GetActiveGroupCallAsync(Guid userId, Guid chatRoomId, CancellationToken ct)
    {
        var isMember = await db.ChatRoomMembers.AnyAsync(
            m => m.RoomId == chatRoomId && m.UserId == userId && m.LeftAt == null, ct);
        if (!isMember) return Fail(CallErrors.NotGroupMember, "You are not a member of this group.");
        var liveId = await LiveGroupCallIdAsync(chatRoomId, ct);
        return new CallResultDto(true, null, null, liveId is { } id ? await GetRoomCoreAsync(id, userId, ct) : null);
    }

    public async Task<CallResultDto> InviteAsync(Guid inviterId, Guid callId, string nik, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nik)) return Fail(CallErrors.PeerNotFound, "Choose a person to add.");
        var room = await db.CallRooms.Include(r => r.ChatRoom).Include(r => r.Participants).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(r => r.Id == callId, ct);
        if (room is null) return Fail(CallErrors.RoomNotFound, "Call not found.");
        if (room.Status is not (CallStatus.Ringing or CallStatus.Active))
            return Fail(CallErrors.CallAlreadyEnded, "Call has already ended.");

        var me = room.Participants.FirstOrDefault(p => p.UserId == inviterId);
        if (me is null || me.Status != CallParticipantStatus.Accepted)
            return Fail(CallErrors.NotParticipant, "Only people in the call can add someone.");
        // A 1:1 call is upgraded only once it is answered (a ringing one has no media yet).
        if (room.Kind == CallKind.User && room.Status != CallStatus.Active)
            return Fail(CallErrors.InvalidSignal, "Wait until the call is answered to add someone.");

        var target = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik.Trim() && u.IsActive, ct);
        if (target is null) return Fail(CallErrors.PeerNotFound, "That person was not found or is inactive.");

        var now = DateTime.UtcNow;
        var existing = room.Participants.FirstOrDefault(p => p.UserId == target.Id);
        if (existing is not null)
        {
            if (existing.Status == CallParticipantStatus.Accepted)
                return Fail(CallErrors.AlreadyInCall, $"{target.Name} is already in the call.");
            if (existing.Status == CallParticipantStatus.Invited && existing.JoinedAt >= now - RingTimeout)
                return Fail(CallErrors.AlreadyInCall, $"{target.Name} is already being called.");
            // Left, declined or missed the ring: ring again (a fresh per-invitee window).
            existing.Status = CallParticipantStatus.Invited;
            existing.JoinedAt = now;
            existing.LeftAt = null;
            existing.IsMuted = false;
            existing.InvitedById = inviterId;
        }
        else
        {
            var added = new CallParticipant
            {
                Id = Guid.NewGuid(), CallId = room.Id, UserId = target.Id, User = target,
                Status = CallParticipantStatus.Invited, JoinedAt = now, InvitedById = inviterId,
            };
            db.CallParticipants.Add(added);   // EF fixup puts it in room.Participants
            if (!room.Participants.Contains(added)) room.Participants.Add(added);
        }

        // First extra person on a P2P call: move it to LiveKit for good (never back to P2P).
        if (room.Kind == CallKind.User) room.Kind = CallKind.AdHoc;
        room.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new CallResultDto(true, null, null, ToRoom(room));
    }

    private Task<Guid?> LiveGroupCallIdAsync(Guid chatRoomId, CancellationToken ct) =>
        db.CallRooms.AsNoTracking()
            .Where(r => r.ChatRoomId == chatRoomId && r.Kind == CallKind.Group
                        && (r.Status == CallStatus.Ringing || r.Status == CallStatus.Active))
            .OrderByDescending(r => r.StartedAt)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);

    /// accept = join (token comes from the join endpoint), decline = only me, leave/end = I hang up.
    private async Task<CallResultDto> GroupSignalAsync(CallRoom room, Guid userId, CallSignalRequest req, CancellationToken ct)
    {
        if (req.Action == "accept") return await JoinGroupCallAsync(userId, room.Id, ct);

        var me = room.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me is null) return Fail(CallErrors.NotParticipant, "You are not in this call.");
        if (room.ChatRoomId is not null && room.ChatRoom is null)
            room.ChatRoom = await db.ChatRooms.FirstOrDefaultAsync(c => c.Id == room.ChatRoomId, ct);

        var now = DateTime.UtcNow;
        switch (req.Action)
        {
            case "decline":
                if (me.Status != CallParticipantStatus.Invited)
                    return Fail(CallErrors.InvalidSignal, "Only an invited member can decline.");
                me.Status = CallParticipantStatus.Declined;
                break;

            case "leave":
            case "end":
                // In a group, hanging up only takes me out; the last one out ends the call.
                if (me.Status == CallParticipantStatus.Accepted)
                {
                    me.Status = CallParticipantStatus.Left;
                    me.LeftAt = now;
                }
                if (!room.Participants.Any(p => p.Status == CallParticipantStatus.Accepted))
                    EndGroupRoom(room, now, timedOut: false);
                break;

            case "update":
                break;

            default:
                return Fail(CallErrors.InvalidSignal, $"Unknown signal \"{req.Action}\".");
        }

        if (req.IsMuted is { } muted) me.IsMuted = muted;
        room.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new CallResultDto(true, null, null, ToRoom(room));
    }

    /// Close a group call. Established → "ended"; nobody ever joined the starter → "missed".
    /// History per participant — starter: completed | canceled | no-answer;
    /// others: completed (joined at some point) | declined | missed.
    private void EndGroupRoom(CallRoom room, DateTime now, bool timedOut)
    {
        var established = room.EstablishedAt is not null;
        room.Status = established ? CallStatus.Ended : CallStatus.Missed;
        room.EndedAt = now;
        room.UpdatedAt = now;
        var duration = room.EstablishedAt is { } est ? Math.Max(0, (int)(now - est).TotalSeconds) : 0;

        foreach (var p in room.Participants)
        {
            if (p.Status == CallParticipantStatus.Accepted)
            {
                p.Status = CallParticipantStatus.Left;
                p.LeftAt = now;
            }
            var isStarter = p.UserId == room.CreatorId;
            var joined = p.Status == CallParticipantStatus.Left;
            var result = isStarter
                ? established ? "completed" : timedOut ? "no-answer" : "canceled"
                : p.Status == CallParticipantStatus.Declined ? "declined"
                : joined && established ? "completed" : "missed";
            db.CallHistories.Add(new CallHistory
            {
                Id = Guid.NewGuid(),
                CallId = room.Id,
                PerformerId = p.UserId,
                Direction = isStarter ? "outgoing" : "incoming",
                Result = result,
                DurationSeconds = result == "completed" ? duration : 0,
                StartedAt = room.StartedAt,
                EndedAt = now,
            });
        }
    }
}
