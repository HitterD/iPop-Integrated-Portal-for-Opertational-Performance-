using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using IPop.Core.Entities;
using IPop.Core.Presence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed partial class ChatService
{
    // ──────────────────────────────────────────────────────────────────────────
    // Room creation and retrieval
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<ChatRoomDto> CreateDirectRoomAsync(Guid creatorId, Guid recipientId, CancellationToken ct)
    {
        // Fast path: reuse existing DM
        var existing = await FindExistingDmAsync(creatorId, recipientId, ct);
        if (existing is not null)
        {
            // Starting a chat with someone again brings a DM deleted for me back into my list.
            var mine = await db.ChatRoomUserSettings.FirstOrDefaultAsync(s => s.UserId == creatorId && s.RoomId == existing.Id, ct);
            if (mine?.DeletedAt is not null) { mine.DeletedAt = null; await db.SaveChangesAsync(ct); }
            return existing;
        }

        var now = DateTime.UtcNow;
        var room = new ChatRoom
        {
            Id = Guid.NewGuid(),
            Type = "dm",
            Name = null,
            IsActive = true,
            CreatedAt = now,
            LastMessageAt = now
        };

        db.ChatRooms.Add(room);
        db.ChatRoomMembers.Add(new ChatRoomMember { Id = Guid.NewGuid(), RoomId = room.Id, UserId = creatorId, Role = "member", JoinedAt = now });
        db.ChatRoomMembers.Add(new ChatRoomMember { Id = Guid.NewGuid(), RoomId = room.Id, UserId = recipientId, Role = "member", JoinedAt = now });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Parallel race: another request created the same DM first — return it
            db.ChangeTracker.Clear();
            var raced = await FindExistingDmAsync(creatorId, recipientId, ct);
            if (raced is not null) return raced;
            throw;
        }

        return await GetRoomAsync(room.Id, creatorId, ct) ?? throw new InvalidOperationException("Room was just created but could not be loaded.");
    }

    public async Task<ChatRoomDto> CreateGroupRoomAsync(Guid creatorId, CreateGroupRequest req, CancellationToken ct)
    {
        var totalMembers = req.MemberIds.Count + 1; // +1 for creator
        if (totalMembers < 3)
            throw new InvalidOperationException("group_too_small");
        if (totalMembers > 256)
            throw new InvalidOperationException("group_full");

        var now = DateTime.UtcNow;
        var room = new ChatRoom
        {
            Id = Guid.NewGuid(),
            Type = "group",
            Name = req.Name,
            IsActive = true,
            CreatedAt = now,
            LastMessageAt = now
        };

        db.ChatRooms.Add(room);

        // Creator as owner
        db.ChatRoomMembers.Add(new ChatRoomMember
        {
            Id = Guid.NewGuid(),
            RoomId = room.Id,
            UserId = creatorId,
            Role = "owner",
            JoinedAt = now
        });

        // Other members
        foreach (var memberId in req.MemberIds.Distinct())
        {
            if (memberId == creatorId) continue;
            db.ChatRoomMembers.Add(new ChatRoomMember
            {
                Id = Guid.NewGuid(),
                RoomId = room.Id,
                UserId = memberId,
                Role = "member",
                JoinedAt = now
            });
        }

        // System message "Room created"
        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = room.Id,
            SenderId = creatorId,
            Type = "system",
            Content = "Room created",
            IdempotencyKey = Guid.NewGuid(),
            CreatedAt = now
        });
        WriteAudit(room.Id, "room_created", creatorId, null, null, null, now);

        await db.SaveChangesAsync(ct);

        return await GetRoomAsync(room.Id, creatorId, ct) ?? throw new InvalidOperationException("Room was just created but could not be loaded.");
    }

    public async Task<ChatRoomDto?> FindExistingDmAsync(Guid userId1, Guid userId2, CancellationToken ct)
    {
        var roomId = await db.ChatRoomMembers
            .Where(m => m.UserId == userId1 && m.LeftAt == null && m.Room.Type == "dm")
            .Select(m => m.RoomId)
            .Intersect(
                db.ChatRoomMembers
                    .Where(m => m.UserId == userId2 && m.LeftAt == null && m.Room.Type == "dm")
                    .Select(m => m.RoomId)
            )
            .FirstOrDefaultAsync(ct);

        if (roomId == Guid.Empty)
            return null;

        return await GetRoomAsync(roomId, userId1, ct);
    }

    public async Task<ChatRoomDto?> GetRoomAsync(Guid roomId, Guid userId, CancellationToken ct)
    {
        var room = await db.ChatRooms
            .AsNoTracking()
            .Where(r => r.Id == roomId)
            .FirstOrDefaultAsync(ct);

        if (room is null) return null;

        // Verify user is a member
        var isMember = await db.ChatRoomMembers
            .AsNoTracking()
            .AnyAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);

        if (!isMember) return null;

        var now = DateTime.UtcNow;
        var members = await db.ChatRoomMembers
            .AsNoTracking()
            .Where(m => m.RoomId == roomId && m.LeftAt == null)
            .Include(m => m.User)
            .Select(m => new MemberDto(
                m.UserId,
                m.User.Nik,
                m.User.Name,
                m.Role,
                PresenceStatus.ComputeStatus(m.User.PresenceLastSeenAt, m.User.PresenceManualStatus, now, m.User.PresenceIsIdle),
                m.User.PresenceLastBeatAt ?? m.User.PresenceLastSeenAt
            ))
            .ToListAsync(ct);

        var onlineCount = members.Count(m => m.PresenceStatus == "on");

        var currentRole = await db.ChatRoomMembers.AsNoTracking().Where(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null).Select(m => m.Role).FirstOrDefaultAsync(ct);
        var allMembers = await db.ChatRoomMembers.AsNoTracking().Where(m => m.RoomId == roomId).Include(m => m.User).ToListAsync(ct);
        var mentionDir = allMembers.Select(m => new MentionUserDto(m.UserId, m.User.Nik, m.User.Name, m.LeftAt == null)).ToList();
        var s = await db.ChatRoomUserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.RoomId == roomId, ct);
        var readReceiptsEnabled = s?.ReadReceiptsEnabled ?? true;
        return new ChatRoomDto(room.Id, room.Type, room.Name, room.Description, members.Count, onlineCount, members, currentRole, mentionDir, readReceiptsEnabled, room.AvatarColor, room.AvatarUrl);
    }

    public async Task<PagedResult<RoomListDto>> GetRoomsAsync(Guid userId, RoomTab tab, int page, int pageSize, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var roomType = tab == RoomTab.Direct ? "dm" : "group";

        // Auto-unmute rooms whose mute period has expired
        var expiredMutes = await db.ChatRoomUserSettings
            .Where(s => s.UserId == userId && s.IsMuted && s.MuteExpiresAt != null && s.MuteExpiresAt < now)
            .ToListAsync(ct);
        foreach (var expired in expiredMutes)
        {
            expired.IsMuted = false;
            expired.MuteExpiresAt = null;
        }
        if (expiredMutes.Count > 0) await db.SaveChangesAsync(ct);

        // Get room IDs the user is a member of (active)
        var memberRoomIds = db.ChatRoomMembers
            .Where(m => m.UserId == userId && m.LeftAt == null)
            .Select(m => m.RoomId);

        var archivedIds = db.ChatRoomUserSettings
            .Where(s => s.UserId == userId && s.IsArchived)
            .Select(s => s.RoomId);

        var hiddenIds = HiddenRoomIds(userId);

        var baseQuery = db.ChatRooms
            .AsNoTracking()
            .Where(r => memberRoomIds.Contains(r.Id) && r.Type == roomType && r.IsActive && !archivedIds.Contains(r.Id) && !hiddenIds.Contains(r.Id));

        var totalCount = await baseQuery.CountAsync(ct);

        var rooms = await (
            from r in baseQuery
            join s in db.ChatRoomUserSettings.AsNoTracking().Where(x => x.UserId == userId) on r.Id equals s.RoomId into sj
            from s in sj.DefaultIfEmpty()
            orderby (s != null && s.IsPinned ? 0 : 1), (s != null ? s.PinOrder : 0), r.LastMessageAt descending
            select new { Room = r, Settings = s }
        )
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync(ct);

        var roomIds = rooms.Select(x => x.Room.Id).ToList();

        // Batch: latest message per room
        var lastMessages = await db.ChatMessages.AsNoTracking()
            .Where(m => roomIds.Contains(m.RoomId) && m.ThreadParentId == null)
            .GroupBy(m => m.RoomId)
            .Select(g => g.OrderByDescending(m => m.CreatedAt).First())
            .ToListAsync(ct);

        var lastByRoom = lastMessages.ToDictionary(m => m.RoomId);
        var lastSenderIds = lastMessages.Select(m => m.SenderId).Distinct().ToList();
        var lastSenderNames = await db.Users.AsNoTracking().Where(u => lastSenderIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Name }).ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        // Batch: unread count per room
        var withReadIds = rooms.Where(x => x.Settings?.LastReadMessageAt != null).Select(x => x.Room.Id).ToList();
        var withoutReadIds = rooms.Where(x => x.Settings?.LastReadMessageAt == null).Select(x => x.Room.Id).ToList();

        var unreadByRoom = new Dictionary<Guid, int>();
        if (withoutReadIds.Count > 0)
        {
            var counts = await db.ChatMessages.AsNoTracking()
                .Where(m => withoutReadIds.Contains(m.RoomId) && m.ThreadParentId == null && m.SenderId != userId && m.Type != "system")
                .GroupBy(m => m.RoomId)
                .Select(g => new { RoomId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.RoomId, x => x.Count, ct);
            foreach (var id in withoutReadIds) unreadByRoom[id] = counts.TryGetValue(id, out var c) ? c : 0;
        }
        if (withReadIds.Count > 0)
        {
            var counts = await (
                from m in db.ChatMessages.AsNoTracking()
                join s in db.ChatRoomUserSettings.Where(x => x.UserId == userId) on m.RoomId equals s.RoomId
                where withReadIds.Contains(m.RoomId) && m.ThreadParentId == null && m.SenderId != userId && m.Type != "system"
                      && s.LastReadMessageAt != null && m.CreatedAt > s.LastReadMessageAt
                group m by m.RoomId into g
                select new { RoomId = g.Key, Count = g.Count() }
            ).ToDictionaryAsync(x => x.RoomId, x => x.Count, ct);
            foreach (var id in withReadIds) unreadByRoom[id] = counts.TryGetValue(id, out var c) ? c : 0;
        }

        // Batch: other member for DM rooms.
        // Projected rather than Include(m => m.User): the room list only needs the partner's
        // name, NIK and presence, but Include pulled every User column (PasswordHash, PhotoPath
        // and the rest) for each room. On a slow link that bloated the result enough to blow
        // the Npgsql read timeout and fail the whole list.
        Dictionary<Guid, (string? Name, string? Nik, DateTime? LastSeen, string? ManualStatus, bool Idle)> otherByRoom = new();
        if (roomType == "dm")
        {
            var allOthers = await db.ChatRoomMembers.AsNoTracking()
                .Where(m => roomIds.Contains(m.RoomId) && m.UserId != userId && m.LeftAt == null)
                .Select(m => new
                {
                    m.RoomId,
                    Name = m.User!.Name,
                    Nik = m.User.Nik,
                    m.User.PresenceLastSeenAt,
                    m.User.PresenceManualStatus,
                    m.User.PresenceIsIdle
                })
                .ToListAsync(ct);
            otherByRoom = allOthers
                .GroupBy(x => x.RoomId)
                .ToDictionary(
                    g => g.Key,
                    g => (g.First().Name, (string?)g.First().Nik,
                          (DateTime?)g.First().PresenceLastSeenAt, g.First().PresenceManualStatus, g.First().PresenceIsIdle));
        }

        // Batch HasUnreadMention
        var mentionRoomIds = new HashSet<Guid>();
        if (roomIds.Count > 0)
        {
            var mentionCandidates = await (
                from cm in db.ChatMentions.AsNoTracking()
                join m in db.ChatMessages.AsNoTracking() on cm.MessageId equals m.Id
                join s in db.ChatRoomUserSettings.AsNoTracking().Where(x => x.UserId == userId) on m.RoomId equals s.RoomId into sj
                from s in sj.DefaultIfEmpty()
                where cm.UserId == userId
                      && !m.IsDeleted
                      && m.SenderId != userId
                      && roomIds.Contains(m.RoomId)
                      && (s == null || s.LastReadMessageAt == null || m.CreatedAt > s.LastReadMessageAt)
                select m.RoomId
            ).Distinct().ToListAsync(ct);
            foreach (var rid in mentionCandidates) mentionRoomIds.Add(rid);
        }

        var roomDtos = new List<RoomListDto>();
        foreach (var item in rooms)
        {
            var r = item.Room;
            var s = item.Settings;

            lastByRoom.TryGetValue(r.Id, out var lm);
            var lastMessagePreview = lm is null ? null
                : RoomPreview.Build(r.Type, lm.Type, lm.Content, lm.IsDeleted,
                    lastSenderNames.GetValueOrDefault(lm.SenderId), lm.SenderId == userId);
            var unreadCount = unreadByRoom[r.Id];

            string? dmName = null;
            string? dmNik = null;
            string? presenceStatus = null;
            if (r.Type == "dm")
            {
                if (otherByRoom.TryGetValue(r.Id, out var other))
                {
                    dmName = other.Name;
                    dmNik = other.Nik;
                    presenceStatus = PresenceStatus.ComputeStatus(
                        other.LastSeen,
                        other.ManualStatus,
                        now,
                        other.Idle
                    );
                }
            }

            var isMuted = s is { IsMuted: true } && (s.MuteExpiresAt is null || s.MuteExpiresAt > now);
            var isPinned = s?.IsPinned ?? false;
            var isArchived = s?.IsArchived ?? false;
            var hasUnreadMention = mentionRoomIds.Contains(r.Id);
            var muteExpires = s?.IsMuted == true ? s.MuteExpiresAt : null;
            var readReceiptsEnabled = s?.ReadReceiptsEnabled ?? true;

            roomDtos.Add(new RoomListDto(
                r.Id,
                r.Type,
                r.Name,
                dmNik,
                dmName,
                lastMessagePreview,
                r.LastMessageAt == default ? null : r.LastMessageAt,
                unreadCount,
                isMuted,
                isArchived,
                isPinned,
                presenceStatus,
                false,
                hasUnreadMention,
                muteExpires,
                readReceiptsEnabled,
                r.AvatarColor,
                r.AvatarUrl));
        }

        return new PagedResult<RoomListDto>(roomDtos, totalCount, page, pageSize);
    }

    public async Task<MemberResultDto> AddMemberAsync(Guid actorId, Guid roomId, Guid userId, CancellationToken ct)
    {
        var room = await db.ChatRooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null)
            return new MemberResultDto(false, "room_not_found");
        if (room.Type != "group")
            return new MemberResultDto(false, "forbidden");

        var actorRole = await GetMemberRoleAsync(actorId, roomId, ct);
        if (actorRole is not ("owner" or "admin"))
            return new MemberResultDto(false, "forbidden");

        var memberCount = await db.ChatRoomMembers
            .CountAsync(m => m.RoomId == roomId && m.LeftAt == null, ct);
        if (memberCount >= 256)
            return new MemberResultDto(false, "group_full");

        var existingMember = await db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);
        if (existingMember is not null)
            return new MemberResultDto(true, null);

        db.ChatRoomMembers.Add(new ChatRoomMember
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            UserId = userId,
            Role = "member",
            JoinedAt = DateTime.UtcNow
        });

        var now2 = DateTime.UtcNow;
        var actorName = await GetUserNameAsync(actorId, ct);
        var targetName = await GetUserNameAsync(userId, ct);
        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            SenderId = actorId,
            Type = "system",
            Content = $"{actorName} added {targetName}",
            IdempotencyKey = Guid.NewGuid(),
            CreatedAt = now2
        });
        WriteAudit(roomId, "member_added", actorId, userId, null, null, now2);

        await db.SaveChangesAsync(ct);
        return new MemberResultDto(true, null);
    }

    public async Task<MemberResultDto> RemoveMemberAsync(Guid actorId, Guid roomId, Guid userId, CancellationToken ct)
    {
        if (actorId == userId)
            return new MemberResultDto(false, "forbidden");

        var actorRole = await GetMemberRoleAsync(actorId, roomId, ct);
        if (actorRole is not ("owner" or "admin"))
            return new MemberResultDto(false, "forbidden");

        var target = await db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);
        if (target is null)
            return new MemberResultDto(false, "message_not_found");

        if (actorRole == "admin" && target.Role is "owner" or "admin")
            return new MemberResultDto(false, "forbidden");

        target.LeftAt = DateTime.UtcNow;

        var now2 = DateTime.UtcNow;
        var actorName = await GetUserNameAsync(actorId, ct);
        var targetName = await GetUserNameAsync(userId, ct);
        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            SenderId = actorId,
            Type = "system",
            Content = $"{actorName} removed {targetName}",
            IdempotencyKey = Guid.NewGuid(),
            CreatedAt = now2
        });
        WriteAudit(roomId, "member_removed", actorId, userId, null, null, now2);

        await db.SaveChangesAsync(ct);
        return new MemberResultDto(true, null);
    }

    public async Task<MemberResultDto> ChangeRoleAsync(Guid actorId, Guid roomId, Guid userId, ChatMemberRole newRole, CancellationToken ct)
    {
        var actorRole = await GetMemberRoleAsync(actorId, roomId, ct);
        if (actorRole != "owner")
            return new MemberResultDto(false, "forbidden");

        if (actorId == userId)
            return new MemberResultDto(false, "forbidden");

        var target = await db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);
        if (target is null)
            return new MemberResultDto(false, "message_not_found");

        var roleStr = newRole switch
        {
            ChatMemberRole.Admin => "admin",
            ChatMemberRole.Member => "member",
            ChatMemberRole.Owner => "owner",
            _ => throw new ArgumentOutOfRangeException(nameof(newRole), newRole, null)
        };
        if (target.Role == roleStr)
            return new MemberResultDto(true, null);
        if (roleStr == "owner")
            return new MemberResultDto(false, "forbidden");

        var oldRole = target.Role;
        target.Role = roleStr;

        var now2 = DateTime.UtcNow;
        var actorName = await GetUserNameAsync(actorId, ct);
        var targetName = await GetUserNameAsync(userId, ct);
        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            SenderId = actorId,
            Type = "system",
            Content = $"{actorName} changed {targetName}'s role to {roleStr}",
            IdempotencyKey = Guid.NewGuid(),
            CreatedAt = now2
        });
        WriteAudit(roomId, "role_changed", actorId, userId, oldRole, roleStr, now2);

        await db.SaveChangesAsync(ct);
        return new MemberResultDto(true, null);
    }

    public async Task<MemberResultDto> LeaveGroupAsync(Guid userId, Guid roomId, CancellationToken ct)
    {
        var member = await db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);
        if (member is null)
            return new MemberResultDto(false, "room_not_found");

        var now = DateTime.UtcNow;

        ChatRoomMember? transferred = null;
        if (member.Role == "owner")
        {
            var adminCandidates = await db.ChatRoomMembers
                .Where(m => m.RoomId == roomId && m.UserId != userId && m.LeftAt == null && m.Role == "admin")
                .Include(m => m.User)
                .OrderBy(m => m.JoinedAt).ThenBy(m => m.User.Nik)
                .ToListAsync(ct);
            var candidate = adminCandidates.FirstOrDefault();
            if (candidate == null)
            {
                var memberCandidates = await db.ChatRoomMembers
                    .Where(m => m.RoomId == roomId && m.UserId != userId && m.LeftAt == null && m.Role == "member")
                    .Include(m => m.User)
                    .OrderBy(m => m.JoinedAt).ThenBy(m => m.User.Nik)
                    .ToListAsync(ct);
                candidate = memberCandidates.FirstOrDefault();
            }

            if (candidate is null)
            {
                var callerSettings = await GetOrCreateSettingsAsync(userId, roomId, ct);
                callerSettings.IsArchived = true;
                var room = await db.ChatRooms.FindAsync([roomId], ct);
                if (room is not null)
                    room.IsActive = false;
            }
            else
            {
                candidate.Role = "owner";
                transferred = candidate;
            }
        }

        member.LeftAt = now;

        var userName = await GetUserNameAsync(userId, ct);
        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            SenderId = userId,
            Type = "system",
            Content = $"{userName} left the group",
            IdempotencyKey = Guid.NewGuid(),
            CreatedAt = now
        });
        WriteAudit(roomId, "member_left", userId, userId, null, null, now);
        if (transferred != null)
            WriteAudit(roomId, "ownership_transferred", userId, transferred.UserId, null, null, now);

        await db.SaveChangesAsync(ct);
        return new MemberResultDto(true, null);
    }
}
