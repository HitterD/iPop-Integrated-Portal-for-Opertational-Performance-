using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using IPop.Core.Entities;
using IPop.Core.Presence;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed class ChatService(IPopDbContext db) : IChatService
{
    // ──────────────────────────────────────────────────────────────────────────
    // Task 5.1 — Room creation and retrieval
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<ChatRoomDto> CreateDirectRoomAsync(Guid creatorId, Guid recipientId, CancellationToken ct)
    {
        // Fast path: reuse existing DM
        var existing = await FindExistingDmAsync(creatorId, recipientId, ct);
        if (existing is not null)
            return existing;

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
        // Find rooms of type "dm" where both users are active members (LeftAt == null)
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
                PresenceStatus.ComputeStatus(m.User.PresenceLastSeenAt, m.User.PresenceManualStatus, now)
            ))
            .ToListAsync(ct);

        var onlineCount = members.Count(m => m.PresenceStatus == "on");

        var currentRole = await db.ChatRoomMembers.AsNoTracking().Where(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null).Select(m => m.Role).FirstOrDefaultAsync(ct);
        var allMembers = await db.ChatRoomMembers.AsNoTracking().Where(m => m.RoomId == roomId).Include(m => m.User).ToListAsync(ct);
        var mentionDir = allMembers.Select(m => new MentionUserDto(m.UserId, m.User.Nik, m.User.Name, m.LeftAt == null)).ToList();
        var s = await db.ChatRoomUserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.RoomId == roomId, ct);
        var readReceiptsEnabled = s?.ReadReceiptsEnabled ?? true;
        return new ChatRoomDto(room.Id, room.Type, room.Name, room.Description, members.Count, onlineCount, members, currentRole, mentionDir, readReceiptsEnabled);
    }

    public async Task<PagedResult<RoomListDto>> GetRoomsAsync(Guid userId, RoomTab tab, int page, int pageSize, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var roomType = tab == RoomTab.Direct ? "dm" : "group";

        // Auto-unmute rooms whose mute period has expired (Task 19.4)
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

        var roomsQuery = db.ChatRooms
            .AsNoTracking()
            .Where(r => memberRoomIds.Contains(r.Id) && r.Type == roomType && r.IsActive && !archivedIds.Contains(r.Id));

        var totalCount = await roomsQuery.CountAsync(ct);

        // Load rooms with user settings, sorted by pinned first then LastMessageAt DESC
        var rooms = await roomsQuery
            .GroupJoin(
                db.ChatRoomUserSettings.Where(s => s.UserId == userId),
                r => r.Id,
                s => s.RoomId,
                (r, settings) => new { Room = r, Settings = settings.FirstOrDefault() }
            )
            .OrderByDescending(x => x.Settings != null && x.Settings.IsPinned)
            .ThenByDescending(x => x.Room.LastMessageAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        if (rooms.Count == 0)
            return new PagedResult<RoomListDto>([], totalCount, page, pageSize);

        var roomIds = rooms.Select(x => x.Room.Id).ToList();

        // Batch: last message per room (1 query)
        var lastByRoom = await db.ChatMessages.AsNoTracking()
            .Where(m => roomIds.Contains(m.RoomId) && m.ThreadParentId == null)
            .GroupBy(m => m.RoomId)
            .Select(g => g.OrderByDescending(m => m.CreatedAt).FirstOrDefault())
            .ToDictionaryAsync(m => m!.RoomId, m => m!, ct);

        // Batch: unread counts — 2 queries total instead of N per-room
        var settingsByRoom = rooms.ToDictionary(x => x.Room.Id, x => x.Settings);
        var unreadByRoom = new Dictionary<Guid, int>();
        var nullReadIds = roomIds.Where(id => settingsByRoom[id]?.LastReadMessageAt is null).ToList();
        var withReadIds = roomIds.Where(id => settingsByRoom[id]?.LastReadMessageAt is not null).ToList();
        if (nullReadIds.Count > 0)
        {
            var counts = await db.ChatMessages.AsNoTracking()
                .Where(m => nullReadIds.Contains(m.RoomId) && m.ThreadParentId == null && m.SenderId != userId && m.Type != "system")
                .GroupBy(m => m.RoomId)
                .Select(g => new { RoomId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.RoomId, x => x.Count, ct);
            foreach (var id in nullReadIds) unreadByRoom[id] = counts.TryGetValue(id, out var c) ? c : 0;
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

        // Batch: other member for DM rooms (1 query)
        Dictionary<Guid, ChatRoomMember> otherByRoom = new();
        if (roomType == "dm")
        {
            var allOthers = await db.ChatRoomMembers.AsNoTracking()
                .Include(m => m.User)
                .Where(m => roomIds.Contains(m.RoomId) && m.UserId != userId && m.LeftAt == null)
                .ToListAsync(ct);
            otherByRoom = allOthers.GroupBy(m => m.RoomId).ToDictionary(g => g.Key, g => g.First());
        }

        // Batch HasUnreadMention: one query per call — avoids N+1 over up to 50 rooms
        var mentionRoomIds = new HashSet<Guid>();
        {
            // Need LastReadMessageAt per room to evaluate mention window
            // Use ChatMention JOIN ChatMessage semantics from design § HasUnreadMention
            var roomIdsForMention = roomIds;
            if (roomIdsForMention.Count > 0)
            {
                // Build map: roomId -> LastReadMessageAt (null means unread window = all)
                // We already have settingsByRoom above; build a lookup then batch query mentions.
                // Batch: SELECT DISTINCT m.RoomId FROM ChatMentions cm JOIN ChatMessages m ON m.Id=cm.MessageId
                // WHERE cm.UserId=@userId AND NOT m.IsDeleted AND m.SenderId != @userId
                // AND (LastReadMessageAt IS NULL OR m.CreatedAt > LastReadMessageAt)
                var mentionCandidates = await (
                    from cm in db.ChatMentions.AsNoTracking()
                    join m in db.ChatMessages.AsNoTracking() on cm.MessageId equals m.Id
                    join s in db.ChatRoomUserSettings.AsNoTracking().Where(x => x.UserId == userId) on m.RoomId equals s.RoomId into sj
                    from s in sj.DefaultIfEmpty()
                    where cm.UserId == userId
                          && !m.IsDeleted
                          && m.SenderId != userId
                          && roomIdsForMention.Contains(m.RoomId)
                          && (s == null || s.LastReadMessageAt == null || m.CreatedAt > s.LastReadMessageAt)
                    select m.RoomId
                ).Distinct().ToListAsync(ct);
                foreach (var rid in mentionCandidates) mentionRoomIds.Add(rid);
            }
        }

        var roomDtos = new List<RoomListDto>();
        foreach (var item in rooms)
        {
            var r = item.Room;
            var s = item.Settings;

            lastByRoom.TryGetValue(r.Id, out var lm);
            var lastMessagePreview = lm is null ? null : lm.IsDeleted ? "This message was deleted" : MentionPlainText.Project(lm.Content, 100);
            var unreadCount = unreadByRoom[r.Id];

            string? recipientNik = null, recipientName = null, presenceStatus = null;
            if (r.Type == "dm" && otherByRoom.TryGetValue(r.Id, out var other))
            {
                recipientNik = other.User.Nik;
                recipientName = other.User.Name;
                presenceStatus = PresenceStatus.ComputeStatus(other.User.PresenceLastSeenAt, other.User.PresenceManualStatus, now);
            }

            var muteExpires = s?.IsMuted == true ? s.MuteExpiresAt : null;
            var readReceiptsEnabled = s?.ReadReceiptsEnabled ?? true;
            roomDtos.Add(new RoomListDto(
                r.Id, r.Type, r.Name, recipientNik, recipientName,
                lastMessagePreview, r.LastMessageAt == default ? null : r.LastMessageAt,
                unreadCount, s?.IsMuted ?? false, s?.IsArchived ?? false, s?.IsPinned ?? false,
                presenceStatus, false, mentionRoomIds.Contains(r.Id), muteExpires,
                readReceiptsEnabled));
        }

        return new PagedResult<RoomListDto>(roomDtos, totalCount, page, pageSize);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Task 5.2 — SendMessage with idempotency
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<MessageResultDto> SendMessageAsync(Guid senderId, SendMessageRequest req, CancellationToken ct)
    {
        if (req.Content is null)
            return new MessageResultDto(false, null, "content_required");
        if (req.Content.Length > 4000)
            return new MessageResultDto(false, null, "content_too_long");

        // Validate sender is active member of room
        var isMember = await db.ChatRoomMembers
            .AnyAsync(m => m.RoomId == req.RoomId && m.UserId == senderId && m.LeftAt == null, ct);
        if (!isMember)
            return new MessageResultDto(false, null, "room_not_found");

        // ipop-22: project/task mention membership guard (sender must be project member)
        var projErr = await ValidateProjectTaskMentionsAsync(senderId, req.Content, ct);
        if (projErr != null)
            return new MessageResultDto(false, null, projErr);

        // Validate ReplyToId belongs to same room + exists + not deleted (H2)
        if (req.ReplyToId is not null)
        {
            var replyOk = await db.ChatMessages.AsNoTracking().AnyAsync(m => m.Id == req.ReplyToId.Value && m.RoomId == req.RoomId && !m.IsDeleted, ct);
            if (!replyOk) return new MessageResultDto(false, null, "message_not_found");
        }

        // Check idempotency: query by (RoomId, IdempotencyKey)
        var existing = await db.ChatMessages
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .FirstOrDefaultAsync(m => m.RoomId == req.RoomId && m.IdempotencyKey == req.IdempotencyKey, ct);

        if (existing is not null)
        {
            if (existing.Content == req.Content)
            {
                // Same content → return existing message as success
                return new MessageResultDto(true, MapToMessageDto(existing, senderId), null);
            }
            else
            {
                // Different content → conflict
                return new MessageResultDto(false, null, "idempotency_conflict");
            }
        }

        var now = DateTime.UtcNow;
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = req.RoomId,
            SenderId = senderId,
            Type = req.Type ?? "text",
            Content = req.Content,
            IdempotencyKey = req.IdempotencyKey,
            ReplyToId = req.ReplyToId,
            CreatedAt = now
        };

        db.ChatMessages.Add(message);

        // Persist Mention_Record atomically with the message (Req 6.1)
        var mentionRecipients = await BuildMentionRecipientsAsync(req.RoomId, senderId, req.Content, ct);
        foreach (var r in mentionRecipients)
        {
            db.ChatMentions.Add(new ChatMention
            {
                Id = Guid.NewGuid(), MessageId = message.Id, UserId = r.UserId,
                IsAllMention = r.ViaAllOnly, CreatedAt = now,
                SuppressNotification = r.SuppressNotification
            });
        }

        // Link pre-uploaded attachments (uploaded via /attachments/upload with MessageId=null)
        if (req.AttachmentIds is { Count: > 0 })
        {
            var orphans = await db.ChatAttachments
                .Where(a => req.AttachmentIds.Contains(a.Id) && a.MessageId == null)
                .ToListAsync(ct);
            foreach (var a in orphans)
                a.MessageId = message.Id;
            // Silently ignore ids that don't exist or already linked — no fail on stale ids
        }

        // Update ChatRoom.LastMessageAt
        var room = await db.ChatRooms.FindAsync([req.RoomId], ct);
        if (room is not null)
            room.LastMessageAt = now;

        // Auto-unarchive: if any member has this room archived, set IsArchived = false
        var archivedSettings = await db.ChatRoomUserSettings
            .Where(s => s.RoomId == req.RoomId && s.IsArchived)
            .ToListAsync(ct);
        foreach (var s in archivedSettings)
            s.IsArchived = false;

        // Membership audit: try to add member audit is handled per-action; for message send we don't add audit entries.

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Race condition: another request with the same idempotency key was persisted first
            var raceExisting = await db.ChatMessages
                .AsNoTracking()
                .Include(m => m.Sender)
                .Include(m => m.Attachments)
                .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
                .FirstOrDefaultAsync(m => m.RoomId == req.RoomId && m.IdempotencyKey == req.IdempotencyKey, ct);

            if (raceExisting is not null && raceExisting.Content == req.Content)
                return new MessageResultDto(true, MapToMessageDto(raceExisting, senderId), null);

            return new MessageResultDto(false, null, "idempotency_conflict");
        }

        // Reload with navigation properties for DTO mapping
        var saved = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .FirstAsync(m => m.Id == message.Id, ct);

        return new MessageResultDto(true, MapToMessageDto(saved, senderId), null);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Task 5.3 — GetMessages with pagination
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<PagedResult<MessageDto>> GetMessagesAsync(Guid roomId, Guid userId, int page, int pageSize, CancellationToken ct)
    {
        // Validate user is member of room
        var isMember = await db.ChatRoomMembers
            .AsNoTracking()
            .AnyAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);
        if (!isMember)
            return new PagedResult<MessageDto>([], 0, page, pageSize);

        // Newest page first (page 1 = latest N), then reverse so the UI can render oldest→newest.
        var query = db.ChatMessages
            .AsNoTracking()
            .Where(m => m.RoomId == roomId && m.ThreadParentId == null);

        var totalCount = await query.CountAsync(ct);

        var messages = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(m => m.Sender)
            .Include(m => m.Room)
                .ThenInclude(r => r.Members)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .Include(m => m.ReplyTo)
                .ThenInclude(r => r!.Sender)
            .ToListAsync(ct);

        messages.Reverse();
        var dtos = messages.Select(m => MapToMessageDto(m, userId)).ToList();

        return new PagedResult<MessageDto>(dtos, totalCount, page, pageSize);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Task 5.4 — Read receipts and delivery
    // ──────────────────────────────────────────────────────────────────────────

    public async Task RecordDeliveryAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        var msgRoomId = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Id == messageId).Select(m => m.RoomId).FirstOrDefaultAsync(ct);
        if (msgRoomId == Guid.Empty) return;
        if (!await db.ChatRoomMembers.AsNoTracking().AnyAsync(m => m.RoomId == msgRoomId && m.UserId == userId && m.LeftAt == null, ct))
            return;

        var receipt = await db.ChatReadReceipts
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId, ct);

        if (receipt is null)
        {
            db.ChatReadReceipts.Add(new ChatReadReceipt
            {
                MessageId = messageId,
                UserId = userId,
                DeliveredAt = DateTime.UtcNow,
                ReadAt = null
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Race — ignore
            }
        }
        else if (receipt.DeliveredAt is null)
        {
            receipt.DeliveredAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RecordReadAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        var msg = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.RoomId, m.CreatedAt })
            .FirstOrDefaultAsync(ct);
        if (msg is null) return;
        if (!await db.ChatRoomMembers.AsNoTracking().AnyAsync(m => m.RoomId == msg.RoomId && m.UserId == userId && m.LeftAt == null, ct))
            return;

        var receipt = await db.ChatReadReceipts
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId, ct);

        var now = DateTime.UtcNow;

        // Always record delivery (the client has the message).
        // Only record "read" (for the sender to see) if the reader has enabled read receipts for this room.
        // This implements the reciprocal DM read receipts toggle.
        var readerSettings = await GetOrCreateSettingsAsync(userId, msg.RoomId, ct);
        bool sendReadReceipt = readerSettings.ReadReceiptsEnabled;

        if (receipt is not null)
        {
            if (receipt.DeliveredAt is null) receipt.DeliveredAt = now;
            if (sendReadReceipt)
                receipt.ReadAt = now;
        }
        else
        {
            db.ChatReadReceipts.Add(new ChatReadReceipt
            {
                MessageId = messageId,
                UserId = userId,
                DeliveredAt = now,
                ReadAt = sendReadReceipt ? now : null
            });
        }

        // Also batch mark previous unread messages in this room from other senders
        var earlierMsgIds = await db.ChatMessages
            .Where(m => m.RoomId == msg.RoomId && m.CreatedAt <= msg.CreatedAt && m.SenderId != userId && m.Id != messageId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(100)
            .Select(m => m.Id)
            .ToListAsync(ct);

        if (earlierMsgIds.Count > 0)
        {
            var existingReceipts = await db.ChatReadReceipts
                .Where(r => earlierMsgIds.Contains(r.MessageId) && r.UserId == userId)
                .ToListAsync(ct);

            var existingMap = existingReceipts.ToDictionary(r => r.MessageId);

            foreach (var mId in earlierMsgIds)
            {
                if (existingMap.TryGetValue(mId, out var r))
                {
                    if (r.DeliveredAt is null) r.DeliveredAt = now;
                    if (sendReadReceipt && r.ReadAt is null) r.ReadAt = now;
                }
                else
                {
                    db.ChatReadReceipts.Add(new ChatReadReceipt
                    {
                        MessageId = mId,
                        UserId = userId,
                        DeliveredAt = now,
                        ReadAt = sendReadReceipt ? now : null
                    });
                }
            }
        }

        if (readerSettings.LastReadMessageAt is null || msg.CreatedAt > readerSettings.LastReadMessageAt)
            readerSettings.LastReadMessageAt = msg.CreatedAt;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Race condition on UPSERT — safe to ignore
        }
    }

    public async Task<IReadOnlyList<SeenByDto>> GetSeenByAsync(Guid messageId, CancellationToken ct)
    {
        // Defense-in-depth: endpoint already checks membership, but service must too for direct callers.
        var msgRoomId2 = await db.ChatMessages.AsNoTracking().Where(m => m.Id == messageId).Select(m => (Guid?)m.RoomId).FirstOrDefaultAsync(ct);
        if (msgRoomId2 is null) return [];
        return await db.ChatReadReceipts
            .AsNoTracking()
            .Where(r => r.MessageId == messageId)
            .Include(r => r.User)
            .OrderBy(r => r.ReadAt)
            .Select(r => new SeenByDto(
                r.UserId,
                r.User.Name,
                r.User.PhotoPath,
                r.DeliveredAt,
                r.ReadAt
            ))
            .ToListAsync(ct);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Stub implementations (tasks 6.x — not yet implemented)
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<MessageResultDto> EditMessageAsync(Guid userId, Guid messageId, string newContent, CancellationToken ct)
    {
        if (newContent is null)
            return new MessageResultDto(false, null, "content_required");
        if (newContent.Length > 4000)
            return new MessageResultDto(false, null, "content_too_long");

        var message = await db.ChatMessages
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (message is null)
            return new MessageResultDto(false, null, "message_not_found");

        if (message.IsDeleted)
            return new MessageResultDto(false, null, "message_not_found");

        if (message.SenderId != userId)
            return new MessageResultDto(false, null, "forbidden");

        if (message.Type is "system" or "emergency")
            return new MessageResultDto(false, null, "forbidden");

        // ipop-22: project/task mention membership guard on edit
        var editProjErr = await ValidateProjectTaskMentionsAsync(userId, newContent, ct);
        if (editProjErr != null)
            return new MessageResultDto(false, null, editProjErr);

        // Check 24-hour edit window
        if ((DateTime.UtcNow - message.CreatedAt).TotalHours > 24)
            return new MessageResultDto(false, null, "edit_window_expired");

        // Store previous content in edit history
        var now = DateTime.UtcNow;
        db.ChatMessageEdits.Add(new ChatMessageEdit
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            PreviousContent = message.Content,
            EditedAt = now
        });

        // Update message
        message.Content = newContent;
        message.EditedAt = now;

        // Sync Mention_Record (Req 6.11/6.12)
        var newRecipients = await BuildMentionRecipientsAsync(message.RoomId, userId, newContent, ct);
        var newIds = newRecipients.Select(r => r.UserId).ToHashSet();
        var existingMentions = await db.ChatMentions.Where(m => m.MessageId == messageId).ToListAsync(ct);
        foreach (var em in existingMentions.Where(em => !newIds.Contains(em.UserId)).ToList())
            db.ChatMentions.Remove(em);
        var existingIds = existingMentions.Select(em => em.UserId).ToHashSet();
        foreach (var r in newRecipients.Where(r => !existingIds.Contains(r.UserId)))
            db.ChatMentions.Add(new ChatMention { Id = Guid.NewGuid(), MessageId = messageId, UserId = r.UserId, IsAllMention = r.ViaAllOnly, CreatedAt = now, SuppressNotification = r.SuppressNotification });

        await db.SaveChangesAsync(ct);

        return new MessageResultDto(true, MapToMessageDto(message, userId), null);
    }

    public async Task<MessageResultDto> DeleteMessageAsync(Guid userId, Guid messageId, bool isAdmin, CancellationToken ct)
    {
        var message = await db.ChatMessages
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (message is null)
            return new MessageResultDto(false, null, "message_not_found");

        if (message.IsDeleted)
            return new MessageResultDto(false, null, "message_not_found");

        if (message.SenderId != userId && !isAdmin)
            return new MessageResultDto(false, null, "forbidden");

        // Soft delete
        message.IsDeleted = true;
        message.DeletedByType = (isAdmin && message.SenderId != userId) ? "admin" : "self";

        await db.SaveChangesAsync(ct);

        return new MessageResultDto(true, MapToMessageDto(message, userId), null);
    }

    public async Task<MessageResultDto> ForwardMessageAsync(Guid userId, Guid messageId, Guid targetRoomId, CancellationToken ct)
    {
        // Load original message
        var original = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (original is null || original.IsDeleted)
            return new MessageResultDto(false, null, "message_not_found");

        // Validate user is member of target room
        var isMemberOfTarget = await db.ChatRoomMembers
            .AnyAsync(m => m.RoomId == targetRoomId && m.UserId == userId && m.LeftAt == null, ct);

        if (!isMemberOfTarget)
            return new MessageResultDto(false, null, "forbidden");

        // H1: require membership in source room too (prevents leak if target membership only)
        var isMemberOfSource = await db.ChatRoomMembers.AnyAsync(m => m.RoomId == original.RoomId && m.UserId == userId && m.LeftAt == null, ct);
        if (!isMemberOfSource)
            return new MessageResultDto(false, null, "forbidden");

        var now = DateTime.UtcNow;
        var formattedContent = original.Content?.StartsWith("[forwarded]") == true
            ? original.Content
            : $"[forwarded] {original.Content}";

        var newMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = targetRoomId,
            SenderId = userId,
            Type = original.Type ?? "text",
            Content = formattedContent,
            IdempotencyKey = Guid.NewGuid(),
            CreatedAt = now
        };

        db.ChatMessages.Add(newMessage);

        // Copy attachments (reference same Synology paths)
        if (original.Attachments is { Count: > 0 })
        {
            foreach (var att in original.Attachments)
            {
                db.ChatAttachments.Add(new ChatAttachment
                {
                    Id = Guid.NewGuid(),
                    MessageId = newMessage.Id,
                    FileName = att.FileName,
                    FileSize = att.FileSize,
                    MimeType = att.MimeType,
                    SynologyServer = att.SynologyServer,
                    SynologyPath = att.SynologyPath,
                    ThumbnailPath = att.ThumbnailPath,
                    CreatedAt = now
                });
            }
        }

        // Update target room's LastMessageAt
        var targetRoom = await db.ChatRooms.FindAsync([targetRoomId], ct);
        if (targetRoom is not null)
            targetRoom.LastMessageAt = now;

        await db.SaveChangesAsync(ct);

        // Reload with navigation properties
        var saved = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .FirstAsync(m => m.Id == newMessage.Id, ct);

        return new MessageResultDto(true, MapToMessageDto(saved, userId), null);
    }

    public async Task<MessageResultDto> SendThreadReplyAsync(Guid senderId, Guid parentMessageId, SendMessageRequest req, CancellationToken ct)
    {
        // Validate content length
        if (req.Content.Length > 4000)
            return new MessageResultDto(false, null, "content_too_long");

        // Load parent message
        var parent = await db.ChatMessages
            .FirstOrDefaultAsync(m => m.Id == parentMessageId, ct);

        if (parent is null)
            return new MessageResultDto(false, null, "message_not_found");

        // Validate sender is a member of the room
        var isMember = await db.ChatRoomMembers
            .AnyAsync(m => m.RoomId == parent.RoomId && m.UserId == senderId && m.LeftAt == null, ct);

        if (!isMember)
            return new MessageResultDto(false, null, "room_not_found");

        // ipop-22: project/task mention membership guard on thread reply
        var threadProjErr = await ValidateProjectTaskMentionsAsync(senderId, req.Content, ct);
        if (threadProjErr != null)
            return new MessageResultDto(false, null, threadProjErr);

        // Enforce max 255 replies per thread (ChatLimits.ThreadReplies)
        if (parent.ThreadReplyCount >= ChatLimits.ThreadReplies)
            return new MessageResultDto(false, null, "thread_full");

        if (req.ReplyToId is not null)
        {
            var replyOk2 = await db.ChatMessages.AsNoTracking().AnyAsync(m => m.Id == req.ReplyToId.Value && m.RoomId == parent.RoomId && !m.IsDeleted, ct);
            if (!replyOk2) return new MessageResultDto(false, null, "message_not_found");
        }

        // Check idempotency
        var existing = await db.ChatMessages
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .FirstOrDefaultAsync(m => m.RoomId == parent.RoomId && m.IdempotencyKey == req.IdempotencyKey, ct);

        if (existing is not null)
        {
            if (existing.Content == req.Content)
                return new MessageResultDto(true, MapToMessageDto(existing, senderId), null);
            else
                return new MessageResultDto(false, null, "idempotency_conflict");
        }

        var now = DateTime.UtcNow;
        var reply = new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = parent.RoomId,
            SenderId = senderId,
            Type = req.Type ?? "text",
            Content = req.Content,
            IdempotencyKey = req.IdempotencyKey,
            ReplyToId = req.ReplyToId,
            ThreadParentId = parentMessageId,
            CreatedAt = now
        };

        db.ChatMessages.Add(reply);

        // Link pre-uploaded attachments for thread replies too
        if (req.AttachmentIds is { Count: > 0 })
        {
            var orphans = await db.ChatAttachments
                .Where(a => req.AttachmentIds.Contains(a.Id) && a.MessageId == null)
                .ToListAsync(ct);
            foreach (var a in orphans)
                a.MessageId = reply.Id;
        }

        // Increment parent thread counters
        parent.ThreadReplyCount++;
        parent.LastThreadReplyAt = now;

        // Update room LastMessageAt
        var room = await db.ChatRooms.FindAsync([parent.RoomId], ct);
        if (room is not null)
            room.LastMessageAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            var raceExisting = await db.ChatMessages
                .AsNoTracking()
                .Include(m => m.Sender)
                .Include(m => m.Attachments)
                .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
                .FirstOrDefaultAsync(m => m.RoomId == parent.RoomId && m.IdempotencyKey == req.IdempotencyKey, ct);

            if (raceExisting is not null && raceExisting.Content == req.Content)
                return new MessageResultDto(true, MapToMessageDto(raceExisting, senderId), null);

            return new MessageResultDto(false, null, "idempotency_conflict");
        }

        // Reload with navigation properties
        var saved = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .FirstAsync(m => m.Id == reply.Id, ct);

        return new MessageResultDto(true, MapToMessageDto(saved, senderId), null);
    }

    public async Task<PagedResult<MessageDto>> GetThreadRepliesAsync(Guid parentMessageId, Guid userId, int page, int pageSize, CancellationToken ct)
    {
        // H4: thread replies require parent-room membership (prevents unauth enumeration)
        var parent = await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == parentMessageId, ct);
        if (parent is null) return new PagedResult<MessageDto>([], 0, page, pageSize);
        var isMember = await db.ChatRoomMembers.AsNoTracking().AnyAsync(m => m.RoomId == parent.RoomId && m.UserId == userId && m.LeftAt == null, ct);
        if (!isMember) return new PagedResult<MessageDto>([], 0, page, pageSize);

        var query = db.ChatMessages
            .AsNoTracking()
            .Where(m => m.ThreadParentId == parentMessageId);

        var totalCount = await query.CountAsync(ct);

        var messages = await query
            .OrderBy(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .Include(m => m.ReplyTo)
                .ThenInclude(r => r!.Sender)
            .ToListAsync(ct);

        var dtos = messages.Select(m => MapToMessageDto(m, userId)).ToList();

        return new PagedResult<MessageDto>(dtos, totalCount, page, pageSize);
    }

    // Removed the no-userId overload (authz bypass). No implementation.

    public async Task<ReactionResultDto> ToggleReactionAsync(Guid userId, Guid messageId, string emoji, CancellationToken ct)
    {
        // Validate message exists and is not deleted
        var message = await db.ChatMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null)
            return new ReactionResultDto(false, null, "message_not_found");
        if (message.IsDeleted)
            return new ReactionResultDto(false, null, "message_deleted");
        var isMember = await db.ChatRoomMembers.AsNoTracking()
            .AnyAsync(m => m.RoomId == message.RoomId && m.UserId == userId && m.LeftAt == null, ct);
        if (!isMember)
            return new ReactionResultDto(false, null, "forbidden");

        // Check if reaction already exists
        var existing = await db.ChatReactions
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId && r.Emoji == emoji, ct);

        if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > 32)
            return new ReactionResultDto(false, null, "invalid_emoji");

        if (existing is not null)
        {
            // Toggle off: remove reaction
            db.ChatReactions.Remove(existing);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* concurrent remove wins — treat as success */ }

            // Get current count for this emoji
            var countAfterRemove = await db.ChatReactions
                .CountAsync(r => r.MessageId == messageId && r.Emoji == emoji, ct);

            return new ReactionResultDto(true, new ReactionDto(messageId, emoji, countAfterRemove, userId, false), null);
        }
        else
        {
            // Check max 20 distinct emoji types per message
            var distinctEmojiCount = await db.ChatReactions
                .Where(r => r.MessageId == messageId)
                .Select(r => r.Emoji)
                .Distinct()
                .CountAsync(ct);

            if (distinctEmojiCount >= 20)
                return new ReactionResultDto(false, null, "reaction_limit");

            // Add new reaction
            db.ChatReactions.Add(new ChatReaction
            {
                MessageId = messageId,
                UserId = userId,
                Emoji = emoji,
                CreatedAt = DateTime.UtcNow
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Race condition: reaction already added — treat as success
                var currentCount = await db.ChatReactions
                    .CountAsync(r => r.MessageId == messageId && r.Emoji == emoji, ct);
                return new ReactionResultDto(true, new ReactionDto(messageId, emoji, currentCount, userId, true), null);
            }

            // Get current count for this emoji
            var countAfterAdd = await db.ChatReactions
                .CountAsync(r => r.MessageId == messageId && r.Emoji == emoji, ct);

            return new ReactionResultDto(true, new ReactionDto(messageId, emoji, countAfterAdd, userId, true), null);
        }
    }

    public async Task<PinnedResultDto> PinMessageAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        // Load message to get RoomId
        var message = await db.ChatMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null)
            return new PinnedResultDto(false, "message_not_found");

        var roomId = message.RoomId;

        // Verify caller is Owner or Admin
        var role = await GetMemberRoleAsync(userId, roomId, ct);
        if (role is not ("owner" or "admin"))
            return new PinnedResultDto(false, "forbidden");

        // Check if already pinned (no-op)
        var alreadyPinned = await db.ChatPinnedMessages
            .AnyAsync(p => p.RoomId == roomId && p.MessageId == messageId, ct);
        if (alreadyPinned)
            return new PinnedResultDto(true, null, roomId);

        // Enforce max 50 pins per room
        var pinCount = await db.ChatPinnedMessages.CountAsync(p => p.RoomId == roomId, ct);
        if (pinCount >= 50)
            return new PinnedResultDto(false, "pin_limit_reached");

        // Add pin record
        db.ChatPinnedMessages.Add(new ChatPinnedMessage
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            MessageId = messageId,
            PinnedByUserId = userId,
            PinnedAt = DateTime.UtcNow
        });

        // System message
        var userName = await GetUserNameAsync(userId, ct);
        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            SenderId = userId,
            Type = "system",
            Content = $"{userName} pinned a message",
            IdempotencyKey = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        });

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Race: another pin won — idempotent success
            return new PinnedResultDto(true, null, roomId);
        }
        return new PinnedResultDto(true, null, roomId);
    }

    public async Task<PinnedResultDto> UnpinMessageAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        // Find the pinned message record
        var pinned = await db.ChatPinnedMessages
            .FirstOrDefaultAsync(p => p.MessageId == messageId, ct);
        if (pinned is null)
            return new PinnedResultDto(false, "message_not_found");

        // Verify caller is Owner or Admin
        var role = await GetMemberRoleAsync(userId, pinned.RoomId, ct);
        if (role is not ("owner" or "admin"))
            return new PinnedResultDto(false, "forbidden");

        var unpinRoomId = pinned.RoomId;
        db.ChatPinnedMessages.Remove(pinned);
        await db.SaveChangesAsync(ct);
        return new PinnedResultDto(true, null, unpinRoomId);
    }

    public async Task<IReadOnlyList<PinnedMessageDto>> GetPinnedMessagesAsync(Guid roomId, CancellationToken ct)
    {
        return await db.ChatPinnedMessages
            .AsNoTracking()
            .Where(p => p.RoomId == roomId && !p.Message.IsDeleted)
            .Include(p => p.Message)
                .ThenInclude(m => m.Sender)
            .OrderByDescending(p => p.PinnedAt)
            .Select(p => new PinnedMessageDto(
                p.Id,
                p.MessageId,
                p.Message.Sender.Name,
                MentionPlainText.Project(p.Message.Content, 120),
                p.PinnedAt
            ))
            .ToListAsync(ct);
    }

    public async Task<MemberResultDto> AddMemberAsync(Guid actorId, Guid roomId, Guid userId, CancellationToken ct)
    {
        // Verify room is group type
        var room = await db.ChatRooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null)
            return new MemberResultDto(false, "room_not_found");
        if (room.Type != "group")
            return new MemberResultDto(false, "forbidden");

        // Verify actor is Owner or Admin
        var actorRole = await GetMemberRoleAsync(actorId, roomId, ct);
        if (actorRole is not ("owner" or "admin"))
            return new MemberResultDto(false, "forbidden");

        // Check member count < 256
        var memberCount = await db.ChatRoomMembers
            .CountAsync(m => m.RoomId == roomId && m.LeftAt == null, ct);
        if (memberCount >= 256)
            return new MemberResultDto(false, "group_full");

        // Check if user is already active member (no-op)
        var existingMember = await db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);
        if (existingMember is not null)
            return new MemberResultDto(true, null);

        // Add member
        db.ChatRoomMembers.Add(new ChatRoomMember
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            UserId = userId,
            Role = "member",
            JoinedAt = DateTime.UtcNow
        });

        var now2 = DateTime.UtcNow;
        // System message
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
        // Verify actor is Owner or Admin
        var actorRole = await GetMemberRoleAsync(actorId, roomId, ct);
        if (actorRole is not ("owner" or "admin"))
            return new MemberResultDto(false, "forbidden");

        // Load target member
        var target = await db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);
        if (target is null)
            return new MemberResultDto(false, "message_not_found");

        // Admin cannot remove Owner or another Admin (unless actor is Owner)
        if (actorRole == "admin" && target.Role is "owner" or "admin")
            return new MemberResultDto(false, "forbidden");

        // Set LeftAt
        target.LeftAt = DateTime.UtcNow;

        var now2 = DateTime.UtcNow;
        // System message
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
        // Only Owner can change roles
        var actorRole = await GetMemberRoleAsync(actorId, roomId, ct);
        if (actorRole != "owner")
            return new MemberResultDto(false, "forbidden");

        // Cannot change own role
        if (actorId == userId)
            return new MemberResultDto(false, "forbidden");

        // Load target member
        var target = await db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);
        if (target is null)
            return new MemberResultDto(false, "message_not_found");

        // Map enum to role string
        var roleStr = newRole switch
        {
            ChatMemberRole.Admin => "admin",
            ChatMemberRole.Member => "member",
            ChatMemberRole.Owner => "owner",
            _ => throw new ArgumentOutOfRangeException(nameof(newRole), newRole, null)
        };
        if (target.Role == roleStr)
            return new MemberResultDto(true, null);
        // Only "admin"/"member" may be granted via this endpoint — ownership transfer is via LeaveGroup.
        if (roleStr == "owner")
            return new MemberResultDto(false, "forbidden");

        var oldRole = target.Role;
        target.Role = roleStr;

        // System message
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
        // Load member
        var member = await db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);
        if (member is null)
            return new MemberResultDto(false, "room_not_found");

        var now = DateTime.UtcNow;

        // If user is Owner, transfer ownership — tie-break by Nik when JoinedAt is equal (Req 9.2)
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
                // Last member — archive Room_Settings for caller (Req 9.10)
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

        // Set LeftAt
        member.LeftAt = now;

        // System message
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

    public async Task<PagedResult<RoomListDto>> SearchRoomsAsync(Guid userId, string query, CancellationToken ct)
    {
        if (query.Length < 2)
            return new PagedResult<RoomListDto>([], 0, 1, 20);

        var pattern = EscapeLikePattern(query);
        var now = DateTime.UtcNow;

        // Get room IDs the user is a member of
        var memberRoomIds = db.ChatRoomMembers
            .Where(m => m.UserId == userId && m.LeftAt == null)
            .Select(m => m.RoomId);

        // Search by room name OR by member name in user's rooms
        var archivedIds = db.ChatRoomUserSettings
            .Where(s => s.UserId == userId && s.IsArchived)
            .Select(s => s.RoomId);

        var roomsByName = db.ChatRooms
            .Where(r => memberRoomIds.Contains(r.Id) && r.IsActive && !archivedIds.Contains(r.Id) && r.Name != null && EF.Functions.ILike(r.Name, pattern));

        var roomsByMember = db.ChatRooms
            .Where(r => memberRoomIds.Contains(r.Id) && r.IsActive && !archivedIds.Contains(r.Id) &&
                r.Members.Any(m => m.LeftAt == null && EF.Functions.ILike(m.User.Name, pattern)));

        var combinedQuery = roomsByName.Union(roomsByMember);
        var totalCount = await combinedQuery.CountAsync(ct);

        var rooms = await combinedQuery
            .OrderByDescending(r => r.LastMessageAt)
            .Take(20)
            .ToListAsync(ct);

        // Batch DM otherMember lookup (avoid N queries)
        Dictionary<Guid, ChatRoomMember> dmOthers = new();
        var dmIds = rooms.Where(r => r.Type == "dm").Select(r => r.Id).ToList();
        if (dmIds.Count > 0)
        {
            var others = await db.ChatRoomMembers.AsNoTracking()
                .Include(m => m.User)
                .Where(m => dmIds.Contains(m.RoomId) && m.UserId != userId && m.LeftAt == null)
                .ToListAsync(ct);
            dmOthers = others.GroupBy(m => m.RoomId).ToDictionary(g => g.Key, g => g.First());
        }

        // Batch HasUnreadMention for SearchRooms as well (Task 7.1)
        var searchRoomIds = rooms.Select(r => r.Id).ToList();
        var searchMentionIds = new HashSet<Guid>();
        if (searchRoomIds.Count > 0)
        {
            var mc = await (
                from cm in db.ChatMentions.AsNoTracking()
                join m in db.ChatMessages.AsNoTracking() on cm.MessageId equals m.Id
                join s in db.ChatRoomUserSettings.AsNoTracking().Where(x => x.UserId == userId) on m.RoomId equals s.RoomId into sj
                from s in sj.DefaultIfEmpty()
                where cm.UserId == userId && !m.IsDeleted && m.SenderId != userId
                      && searchRoomIds.Contains(m.RoomId)
                      && (s == null || s.LastReadMessageAt == null || m.CreatedAt > s.LastReadMessageAt)
                select m.RoomId
            ).Distinct().ToListAsync(ct);
            foreach (var rid in mc) searchMentionIds.Add(rid);
        }

        var roomDtos = new List<RoomListDto>();
        foreach (var r in rooms)
        {
            string? recipientNik = null;
            string? recipientName = null;
            string? presenceStatus = null;

            if (r.Type == "dm" && dmOthers.TryGetValue(r.Id, out var otherMember))
            {
                recipientNik = otherMember.User.Nik;
                recipientName = otherMember.User.Name;
                presenceStatus = PresenceStatus.ComputeStatus(
                    otherMember.User.PresenceLastSeenAt,
                    otherMember.User.PresenceManualStatus, now);
            }

            roomDtos.Add(new RoomListDto(
                r.Id, r.Type, r.Name, recipientNik, recipientName,
                null, r.LastMessageAt == default ? null : r.LastMessageAt,
                0, false, false, false, presenceStatus, false, searchMentionIds.Contains(r.Id), null
            ));
        }

        return new PagedResult<RoomListDto>(roomDtos, totalCount, 1, 20);
    }

    public async Task<PagedResult<MessageDto>> SearchMessagesAsync(Guid userId, Guid roomId, string query, int page, int pageSize, CancellationToken ct)
    {
        if (query.Length < 2)
            return new PagedResult<MessageDto>([], 0, page, pageSize);

        // Verify user is member of room
        var isMember = await db.ChatRoomMembers
            .AsNoTracking()
            .AnyAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);
        if (!isMember)
            return new PagedResult<MessageDto>([], 0, page, pageSize);

        var pattern = EscapeLikePattern(query);

        var messagesQuery = db.ChatMessages
            .AsNoTracking()
            .Where(m => m.RoomId == roomId && m.ThreadParentId == null && !m.IsDeleted
                && EF.Functions.ILike(m.Content, pattern));

        var totalCount = await messagesQuery.CountAsync(ct);

        var messages = await messagesQuery
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .Include(m => m.ReplyTo)
                .ThenInclude(r => r!.Sender)
            .ToListAsync(ct);

        var dtos = messages.Select(m => MapToMessageDto(m, userId)).ToList();
        return new PagedResult<MessageDto>(dtos, totalCount, page, pageSize);
    }

    public async Task<MessageResultDto> SendEmergencyBroadcastAsync(Guid senderId, Guid roomId, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 4000)
            return new MessageResultDto(false, null, content.Length > 4000 ? "content_too_long" : "content_required");
        // Verify sender is Owner or Admin
        var role = await GetMemberRoleAsync(senderId, roomId, ct);
        if (role is not ("owner" or "admin"))
            return new MessageResultDto(false, null, "forbidden");

        var room = await db.ChatRooms.FindAsync([roomId], ct);
        if (room is null)
            return new MessageResultDto(false, null, "room_not_found");

        // Sliding 24h window: count emergency messages in last 24h instead of sticky counter
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var emergencyInWindow = await db.ChatMessages
            .CountAsync(m => m.RoomId == roomId && m.Type == "emergency" && m.CreatedAt > cutoff, ct);
        if (emergencyInWindow >= 3)
            return new MessageResultDto(false, null, "emergency_rate_limited");

        var now = DateTime.UtcNow;

        // Create emergency message
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            SenderId = senderId,
            Type = "emergency",
            Content = content,
            IdempotencyKey = Guid.NewGuid(),
            CreatedAt = now
        };

        db.ChatMessages.Add(message);

        room.LastMessageAt = now;

        // Auto-unarchive for all members
        var archivedSettings = await db.ChatRoomUserSettings
            .Where(s => s.RoomId == roomId && s.IsArchived)
            .ToListAsync(ct);
        foreach (var s in archivedSettings)
            s.IsArchived = false;

        await db.SaveChangesAsync(ct);

        // Reload with navigation properties
        var saved = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .FirstAsync(m => m.Id == message.Id, ct);

        return new MessageResultDto(true, MapToMessageDto(saved, senderId), null);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    private MessageDto MapToMessageDto(ChatMessage m, Guid currentUserId)
    {
        // Handle deleted message content replacement
        var content = m.IsDeleted
            ? m.DeletedByType == "admin"
                ? "This message was deleted by an admin"
                : "This message was deleted"
            : m.Content;

        // Reply preview
        ReplyPreviewDto? replyPreview = null;
        if (m.ReplyTo is not null)
        {
            var previewContent = m.ReplyTo.IsDeleted
                ? "Original message was deleted"
                : Truncate(m.ReplyTo.Content, 150);
            replyPreview = new ReplyPreviewDto(m.ReplyTo.Sender?.Name ?? "Unknown", previewContent);
        }

        // Reactions grouped by emoji
        var reactions = m.Reactions?
            .GroupBy(r => r.Emoji)
            .Select(g => new ReactionGroupDto(
                g.Key,
                g.Count(),
                g.Any(r => r.UserId == currentUserId)
            ))
            .ToList() as IReadOnlyList<ReactionGroupDto> ?? [];

        // Attachments
        var attachments = m.Attachments?
            .Select(a => new AttachmentDto(
                a.Id,
                a.FileName,
                a.FileSize,
                a.MimeType,
                a.ThumbnailPath
            ))
            .ToList() as IReadOnlyList<AttachmentDto> ?? [];

        // Tiered delivery status for the *sender's own messages* (delivered vs read).
        // Populated only when viewing your own sent messages.
        // Respects the recipient's ReadReceiptsEnabled toggle (we only set ReadAt when enabled).
        string? deliveryStatus = null;
        if (m.SenderId == currentUserId)
        {
            var receipts = m.ReadReceipts ?? Enumerable.Empty<ChatReadReceipt>();
            // Consider other participants' receipts (exclude self)
            var others = receipts.Where(r => r.UserId != currentUserId).ToList();
            var isGroup = m.Room?.Type == "group";
            if (!isGroup)
            {
                if (others.Any(r => r.ReadAt != null))
                    deliveryStatus = "read";
                else if (others.Any(r => r.DeliveredAt != null))
                    deliveryStatus = "delivered";
                else
                    deliveryStatus = "sent";
            }
            else
            {
                // In group chat: all active other members must have read for "read"
                var activeOtherMembers = m.Room?.Members?.Count(mem => mem.LeftAt == null && mem.UserId != currentUserId) ?? 0;
                var readCount = others.Count(r => r.ReadAt != null);
                if (activeOtherMembers > 0 && readCount >= activeOtherMembers)
                    deliveryStatus = "read";
                else if (others.Any(r => r.DeliveredAt != null || r.ReadAt != null))
                    deliveryStatus = "delivered";
                else
                    deliveryStatus = "sent";
            }
        }

        return new MessageDto(
            m.Id, m.RoomId, m.SenderId,
            m.Sender?.Name ?? "Unknown", m.Sender?.Nik ?? "Unknown",
            m.Type, content, m.CreatedAt, m.EditedAt, m.IsDeleted,
            m.DeletedByType, m.ReplyToId, replyPreview,
            m.ThreadParentId, m.ThreadReplyCount, m.LastThreadReplyAt,
            attachments, reactions,
            [], false, m.IdempotencyKey,
            deliveryStatus);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        => ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<string?> GetMemberRoleAsync(Guid userId, Guid roomId, CancellationToken ct)
        => await db.ChatRoomMembers.AsNoTracking()
            .Where(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null)
            .Select(m => m.Role).FirstOrDefaultAsync(ct);

    private Task<bool> IsMemberAsync(Guid userId, Guid roomId, CancellationToken ct)
        => db.ChatRoomMembers.AsNoTracking()
            .AnyAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);

    private async Task<ChatRoomUserSettings> GetOrCreateSettingsAsync(Guid userId, Guid roomId, CancellationToken ct)
    {
        var local = db.ChatRoomUserSettings.Local.FirstOrDefault(s => s.UserId == userId && s.RoomId == roomId);
        if (local is not null) return local;

        var settings = await db.ChatRoomUserSettings.FirstOrDefaultAsync(s => s.UserId == userId && s.RoomId == roomId, ct);
        if (settings is null)
        {
            settings = new ChatRoomUserSettings { UserId = userId, RoomId = roomId };
            db.ChatRoomUserSettings.Add(settings);
        }
        return settings;
    }

    private async Task<string> GetUserNameAsync(Guid userId, CancellationToken ct)
        => await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "Unknown";

    private void WriteAudit(Guid roomId, string action, Guid actorId, Guid? targetUserId, string? oldValue, string? newValue, DateTime at)
    {
        db.ChatMembershipAudits.Add(new ChatMembershipAudit
        {
            Id = Guid.NewGuid(), RoomId = roomId, Action = action,
            ActorId = actorId, TargetUserId = targetUserId,
            OldValue = oldValue, NewValue = newValue, CreatedAt = at,
        });
    }

    private async Task<IReadOnlyList<MentionRecipient>> BuildMentionRecipientsAsync(Guid roomId, Guid senderId, string content, CancellationToken ct)
    {
        var segs = MentionParser.Parse(content);
        var roomType = await db.ChatRooms.AsNoTracking().Where(r => r.Id == roomId).Select(r => r.Type).FirstOrDefaultAsync(ct) ?? "group";
        var members = await db.ChatRoomMembers.AsNoTracking().Where(m => m.RoomId == roomId).ToListAsync(ct);
        var dict = members.ToDictionary(m => m.UserId, m => m.LeftAt == null);
        return MentionResolver.Resolve(new MentionResolutionInput(senderId, roomType, segs, dict));
    }

    /// <summary>
    /// ipop-22: Validate that any @project (p:GUID) or @task (t:GUID) mentions in the content
    /// are only used by users who are members of the referenced project (or the project owning the task).
    /// Returns an error code (e.g. "not_project_member") or null if OK.
    /// </summary>
    private async Task<string?> ValidateProjectTaskMentionsAsync(Guid senderId, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var segs = MentionParser.Parse(content);
        var projectIds = new HashSet<Guid>();
        var taskIds = new HashSet<Guid>();

        foreach (var s in segs)
        {
            if (s is MentionSegment.Project p) projectIds.Add(p.ProjectId);
            else if (s is MentionSegment.Task t) taskIds.Add(t.TaskId);
        }

        if (projectIds.Count == 0 && taskIds.Count == 0) return null;

        if (taskIds.Count > 0)
        {
            var taskProjectIds = await db.ProjectTasks.AsNoTracking()
                .Where(t => taskIds.Contains(t.Id))
                .Select(t => t.ProjectId)
                .Distinct()
                .ToListAsync(ct);

            foreach (var pid in taskProjectIds)
                if (pid != Guid.Empty) projectIds.Add(pid);
        }

        if (projectIds.Count == 0) return null;

        var memberOf = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == senderId && projectIds.Contains(m.ProjectId))
            .Select(m => m.ProjectId)
            .ToHashSetAsync(ct);

        foreach (var pid in projectIds)
        {
            if (!memberOf.Contains(pid))
                return "not_project_member";
        }

        return null;
    }

    Task<RoomSettingsResultDto> IChatService.MuteRoomAsync(Guid userId, Guid roomId, MuteDuration duration, CancellationToken ct)
        => MuteRoomAsync(userId, roomId, duration, ct);
    Task<RoomSettingsResultDto> IChatService.UnmuteRoomAsync(Guid userId, Guid roomId, CancellationToken ct)
        => UnmuteRoomAsync(userId, roomId, ct);
    Task<RoomSettingsResultDto> IChatService.ArchiveRoomAsync(Guid userId, Guid roomId, CancellationToken ct)
        => ArchiveRoomAsync(userId, roomId, ct);
    Task<RoomSettingsResultDto> IChatService.UnarchiveRoomAsync(Guid userId, Guid roomId, CancellationToken ct)
        => UnarchiveRoomAsync(userId, roomId, ct);

    public async Task<RoomSettingsResultDto> MuteRoomAsync(Guid userId, Guid roomId, MuteDuration duration, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, roomId, ct)) return new RoomSettingsResultDto(false, "forbidden");
        var settings = await GetOrCreateSettingsAsync(userId, roomId, ct);
        settings.IsMuted = true;
        settings.MuteExpiresAt = duration switch
        {
            MuteDuration.OneHour => DateTime.UtcNow.AddHours(1),
            MuteDuration.EightHours => DateTime.UtcNow.AddHours(8),
            MuteDuration.OneWeek => DateTime.UtcNow.AddDays(7),
            MuteDuration.Forever => null,
            _ => null
        };
        await db.SaveChangesAsync(ct);
        return new RoomSettingsResultDto(true, null);
    }

    public async Task<RoomSettingsResultDto> UnmuteRoomAsync(Guid userId, Guid roomId, CancellationToken ct)
    {
        var settings = await db.ChatRoomUserSettings.FirstOrDefaultAsync(s => s.UserId == userId && s.RoomId == roomId, ct);
        if (settings is null) return new RoomSettingsResultDto(true, null);
        settings.IsMuted = false; settings.MuteExpiresAt = null;
        await db.SaveChangesAsync(ct);
        return new RoomSettingsResultDto(true, null);
    }

    public async Task<RoomSettingsResultDto> ArchiveRoomAsync(Guid userId, Guid roomId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, roomId, ct)) return new RoomSettingsResultDto(false, "forbidden");
        var settings = await GetOrCreateSettingsAsync(userId, roomId, ct);
        settings.IsArchived = true;
        await db.SaveChangesAsync(ct);
        return new RoomSettingsResultDto(true, null);
    }

    public async Task<RoomSettingsResultDto> UnarchiveRoomAsync(Guid userId, Guid roomId, CancellationToken ct)
    {
        var settings = await db.ChatRoomUserSettings.FirstOrDefaultAsync(s => s.UserId == userId && s.RoomId == roomId, ct);
        if (settings is null) return new RoomSettingsResultDto(true, null);
        settings.IsArchived = false;
        await db.SaveChangesAsync(ct);
        return new RoomSettingsResultDto(true, null);
    }

    public async Task<RoomSettingsResultDto> SetReadReceiptsAsync(Guid userId, Guid roomId, bool enabled, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, roomId, ct)) return new RoomSettingsResultDto(false, "forbidden");
        var s = await GetOrCreateSettingsAsync(userId, roomId, ct);
        if (s.ReadReceiptsEnabled == enabled) return new RoomSettingsResultDto(true, null);
        s.ReadReceiptsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        return new RoomSettingsResultDto(true, null);
    }

    // ── Implementations for new IChatService members added for build completeness (stubs that satisfy spec minimally) ──
    public async Task<RoomSettingsResultDto> UpdateGroupIdentityAsyncImpl(Guid actorId, Guid roomId, string name, string? description, CancellationToken ct)
    {
        var trimmedName = name.Trim();
        var trimmedDesc = description?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > 100) return new RoomSettingsResultDto(false, "invalid_group_name");
        if (trimmedDesc != null && trimmedDesc.Length > 500) return new RoomSettingsResultDto(false, "invalid_group_description");
        var room = await db.ChatRooms.FindAsync([roomId], ct);
        if (room is null) return new RoomSettingsResultDto(false, "room_not_found");
        if (room.Type == "dm") return new RoomSettingsResultDto(false, "not_a_group");
        var role = await GetMemberRoleAsync(actorId, roomId, ct);
        if (role is not ("owner" and var _ or "admin")) return new RoomSettingsResultDto(false, "forbidden");
        var changed = room.Name != trimmedName || room.Description != trimmedDesc;
        room.Name = trimmedName; room.Description = trimmedDesc;
        var at2 = DateTime.UtcNow;
        if (changed)
        {
            var actorName = await GetUserNameAsync(actorId, ct);
            db.ChatMessages.Add(new ChatMessage { Id = Guid.NewGuid(), RoomId = roomId, SenderId = actorId, Type = "system", Content = $"{actorName} renamed the group to {trimmedName}", IdempotencyKey = Guid.NewGuid(), CreatedAt = at2 });
            WriteAudit(roomId, "group_renamed", actorId, null, room.Name, trimmedName, at2);
        }
        else
        {
            // No-op per Requirement 10.11 — succeed without audit or system message
        }
        await db.SaveChangesAsync(ct);
        return new RoomSettingsResultDto(true, null);
    }

    public async Task<PagedResult<MembershipAuditDto>> GetMembershipAuditAsyncImpl(Guid callerId, Guid roomId, int page, int pageSize, CancellationToken ct)
    {
        var role = await GetMemberRoleAsync(callerId, roomId, ct);
        if (role is not ("owner" or "admin")) return new PagedResult<MembershipAuditDto>([], 0, page, pageSize);
        if (pageSize <= 0) pageSize = 20; if (pageSize > 100) pageSize = 100;
        var q = db.ChatMembershipAudits.AsNoTracking().Where(a => a.RoomId == roomId).OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id);
        var total = await q.CountAsync(ct);
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize).Include(a => a.Actor).Include(a => a.TargetUser)
            .Select(a => new MembershipAuditDto(a.Id, a.Action, a.Actor.Name, a.TargetUser != null ? a.TargetUser.Name : null, a.OldValue, a.NewValue, a.CreatedAt)).ToListAsync(ct);
        return new PagedResult<MembershipAuditDto>(items, total, page, pageSize);
    }

    public async Task<SeenListDto> GetSeenDetailAsyncImpl(Guid callerId, Guid messageId, CancellationToken ct)
    {
        var msg = await db.ChatMessages.AsNoTracking().Where(m => m.Id == messageId).Select(m => new { m.RoomId, m.CreatedAt, m.SenderId, m.IsDeleted }).FirstOrDefaultAsync(ct);
        if (msg is null || msg.IsDeleted) return new SeenListDto([], 0, 0);
        if (!await IsMemberAsync(callerId, msg.RoomId, ct)) return new SeenListDto([], 0, 0);
        var activeCount = await db.ChatRoomMembers.CountAsync(m => m.RoomId == msg.RoomId && m.LeftAt == null && m.UserId != msg.SenderId, ct);
        var readers = await db.ChatReadReceipts.AsNoTracking().Where(r => r.MessageId == messageId)
            .Join(db.ChatRoomUserSettings.AsNoTracking().Where(s => s.RoomId == msg.RoomId),
                r => r.UserId, s => s.UserId, (r, s) => new { r, s })
            .Where(x => x.s.LastReadMessageAt != null && x.s.LastReadMessageAt >= msg.CreatedAt)
            .Join(db.ChatRoomMembers.AsNoTracking().Where(m => m.RoomId == msg.RoomId && m.LeftAt == null),
                x => x.r.UserId, m => m.UserId, (x, m) => x.r)
            .Include(r => r.User).OrderByDescending(r => r.ReadAt).Take(255)
            .Select(r => new SeenByDto(r.UserId, r.User.Name, r.User.PhotoPath, r.DeliveredAt, r.ReadAt)).ToListAsync(ct);
        return new SeenListDto(readers, readers.Count, activeCount);
    }

    // Interface forwarding — the actual methods above satisfy IChatService via explicit mapping below.
    async Task<RoomSettingsResultDto> IChatService.UpdateGroupIdentityAsync(Guid actorId, Guid roomId, string name, string? description, CancellationToken ct)
        => await UpdateGroupIdentityAsyncImpl(actorId, roomId, name, description, ct);
    async Task<PagedResult<MembershipAuditDto>> IChatService.GetMembershipAuditAsync(Guid callerId, Guid roomId, int page, int pageSize, CancellationToken ct)
        => await GetMembershipAuditAsyncImpl(callerId, roomId, page, pageSize, ct);
    async Task<SeenListDto> IChatService.GetSeenDetailAsync(Guid callerId, Guid messageId, CancellationToken ct)
        => await GetSeenDetailAsyncImpl(callerId, messageId, ct);

    public async Task<RoomSettingsResultDto> PinRoomAsyncImpl(Guid userId, Guid roomId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, roomId, ct)) return new RoomSettingsResultDto(false, "forbidden");
        var s = await GetOrCreateSettingsAsync(userId, roomId, ct);
        if (s.IsPinned) return new RoomSettingsResultDto(true, null);
        var pinnedCount = await db.ChatRoomUserSettings.CountAsync(x => x.UserId == userId && x.IsPinned, ct);
        if (pinnedCount >= ChatLimits.PinnedRoomsPerUser) return new RoomSettingsResultDto(false, "pin_limit_reached");
        s.IsPinned = true; await db.SaveChangesAsync(ct); return new RoomSettingsResultDto(true, null);
    }
    public async Task<RoomSettingsResultDto> UnpinRoomAsyncImpl(Guid userId, Guid roomId, CancellationToken ct)
    {
        var s = await db.ChatRoomUserSettings.FirstOrDefaultAsync(x => x.UserId == userId && x.RoomId == roomId, ct);
        if (s is null || !s.IsPinned) return new RoomSettingsResultDto(true, null);
        s.IsPinned = false; await db.SaveChangesAsync(ct); return new RoomSettingsResultDto(true, null);
    }
    async Task<RoomSettingsResultDto> IChatService.PinRoomAsync(Guid userId, Guid roomId, CancellationToken ct) => await PinRoomAsyncImpl(userId, roomId, ct);
    async Task<RoomSettingsResultDto> IChatService.UnpinRoomAsync(Guid userId, Guid roomId, CancellationToken ct) => await UnpinRoomAsyncImpl(userId, roomId, ct);

    public async Task<PagedResult<RoomListDto>> GetArchivedRoomsAsyncImpl(Guid userId, int page, int pageSize, CancellationToken ct)
    {
        var archivedIds = db.ChatRoomUserSettings.Where(s => s.UserId == userId && s.IsArchived).Select(s => s.RoomId);
        var q = db.ChatRooms.AsNoTracking().Where(r => archivedIds.Contains(r.Id) && r.IsActive);
        var total = await q.CountAsync(ct);
        var rooms = await q.OrderByDescending(r => r.LastMessageAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var dtos = rooms.Select(r => new RoomListDto(r.Id, r.Type, r.Name, null, null, null, r.LastMessageAt == default ? null : r.LastMessageAt, 0, false, true, true, null, false, false, null)).ToList();
        return new PagedResult<RoomListDto>(dtos, total, page, pageSize);
    }
    async Task<PagedResult<RoomListDto>> IChatService.GetArchivedRoomsAsync(Guid userId, int page, int pageSize, CancellationToken ct)
        => await GetArchivedRoomsAsyncImpl(userId, page, pageSize, ct);

    private static string EscapeLikePattern(string query)
        => $"%{query.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
    // Npgsql EF translates EF.Functions.ILike(pattern) with '\' as ESCAPE by PG default;
    // ponytail: use 3-arg overload EF.Functions.ILike(col, pattern, @"\") if Npgsql adds strict mode.
}

