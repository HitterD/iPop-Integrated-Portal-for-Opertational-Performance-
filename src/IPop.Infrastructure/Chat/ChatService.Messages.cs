using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using IPop.Core.Entities;
using IPop.Core.Presence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed partial class ChatService
{
    // ──────────────────────────────────────────────────────────────────────────
    // SendMessage with idempotency
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

        // Check if recipient in direct chat room has blocked the sender
        var chatRoom = await db.ChatRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == req.RoomId, ct);
        if (chatRoom?.Type is "direct" or "dm")
        {
            var otherMemberId = await db.ChatRoomMembers.AsNoTracking()
                .Where(m => m.RoomId == req.RoomId && m.UserId != senderId && m.LeftAt == null)
                .Select(m => m.UserId)
                .FirstOrDefaultAsync(ct);
            if (otherMemberId != Guid.Empty)
            {
                var isBlocked = await db.UserBlocks.AsNoTracking()
                    .AnyAsync(b => b.UserId == otherMemberId && b.BlockedUserId == senderId, ct);
                if (isBlocked)
                    return new MessageResultDto(false, null, "blocked");
            }
        }

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
            .Include(m => m.ReplyTo)
                .ThenInclude(r => r!.Sender)
            .FirstOrDefaultAsync(m => m.RoomId == req.RoomId && m.IdempotencyKey == req.IdempotencyKey, ct);

        if (existing is not null)
        {
            if (existing.Content == req.Content)
            {
                // Same content → return existing message as success
                return new MessageResultDto(true, await MapWithMentionsAsync(existing, senderId, ct), null);
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
        }

        // Update room LastMessageAt
        var room = await db.ChatRooms.FindAsync([req.RoomId], ct);
        if (room is not null)
            room.LastMessageAt = now;

        // Auto-unarchive for members who archived this room (Task 19.3)
        var archivedSettings = await db.ChatRoomUserSettings
            .Where(s => s.RoomId == req.RoomId && s.IsArchived)
            .ToListAsync(ct);
        foreach (var s in archivedSettings)
            s.IsArchived = false;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Concurrent race on (RoomId, IdempotencyKey)
            db.ChangeTracker.Clear();
            var raceExisting = await db.ChatMessages
                .Include(m => m.Sender)
                .Include(m => m.Attachments)
                .Include(m => m.Reactions)
                .Include(m => m.ReadReceipts)
                .Include(m => m.ReplyTo)
                    .ThenInclude(r => r!.Sender)
                .FirstOrDefaultAsync(m => m.RoomId == req.RoomId && m.IdempotencyKey == req.IdempotencyKey, ct);

            if (raceExisting is not null && raceExisting.Content == req.Content)
                return new MessageResultDto(true, await MapWithMentionsAsync(raceExisting, senderId, ct), null);

            return new MessageResultDto(false, null, "idempotency_conflict");
        }

        if (message.Type == "sticker") await StickerService.RecordUseAsync(db, senderId, message.Content, ct);

        // Reload with navigation properties for DTO mapping. ReplyTo must come along, or the
        // ack/broadcast carries no quote and the sender's bubble loses it until a reload.
        var saved = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .Include(m => m.ReplyTo)
                .ThenInclude(r => r!.Sender)
            .FirstAsync(m => m.Id == message.Id, ct);

        return new MessageResultDto(true, await MapWithMentionsAsync(saved, senderId, ct), null);
    }

    public Task<PagedResult<MessageDto>> GetMessagesAsync(Guid roomId, Guid userId, int page, int pageSize, CancellationToken ct)
        => GetMessagesCoreAsync(roomId, userId, page, pageSize, before: null, ct);

    /// Keyset page: the newest pageSize messages strictly older than `before`. Offset pages
    /// shift when new messages arrive (page 2 re-serves rows already on screen), which is
    /// what fed duplicate ids into the client list. TotalCount here counts only the
    /// messages older than the cursor, so more remain while TotalCount exceeds Items.Count.
    public Task<PagedResult<MessageDto>> GetMessagesBeforeAsync(Guid roomId, Guid userId, DateTime before, int pageSize, CancellationToken ct)
        => GetMessagesCoreAsync(roomId, userId, 1, pageSize, before, ct);

    private async Task<PagedResult<MessageDto>> GetMessagesCoreAsync(Guid roomId, Guid userId, int page, int pageSize, DateTime? before, CancellationToken ct)
    {
        var isMember = await db.ChatRoomMembers
            .AsNoTracking()
            .AnyAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);
        if (!isMember)
            return new PagedResult<MessageDto>([], 0, page, pageSize);

        var settings = await db.ChatRoomUserSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.RoomId == roomId && s.UserId == userId, ct);

        var query = db.ChatMessages
            .AsNoTracking()
            .Where(m => m.RoomId == roomId && m.ThreadParentId == null);

        if (settings?.ClearedAt is not null)
            query = query.Where(m => m.CreatedAt > settings.ClearedAt.Value);

        if (before is not null)
        {
            var cursor = before.Value;
            query = query.Where(m => m.CreatedAt < cursor);
        }

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
        var dtos = await MapWithMentionsAsync(messages, userId, ct);

        return new PagedResult<MessageDto>(dtos, totalCount, page, pageSize);
    }

    public async Task<MessageResultDto> EditMessageAsync(Guid userId, Guid messageId, string newContent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newContent))
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
            return new MessageResultDto(false, null, "message_deleted");

        if (message.SenderId != userId)
            return new MessageResultDto(false, null, "forbidden");

        var now = DateTime.UtcNow;
        if ((now - message.CreatedAt).TotalHours >= 24)
            return new MessageResultDto(false, null, "edit_window_expired");

        // Save edit history
        db.ChatMessageEdits.Add(new ChatMessageEdit
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            PreviousContent = message.Content,
            EditedAt = now
        });

        message.Content = newContent;
        message.EditedAt = now;

        // Re-parse mentions on edit
        var newRecipients = await BuildMentionRecipientsAsync(message.RoomId, userId, newContent, ct);
        var newIds = newRecipients.Select(r => r.UserId).ToHashSet();
        var existingMentions = await db.ChatMentions.Where(m => m.MessageId == messageId).ToListAsync(ct);
        foreach (var em in existingMentions.Where(em => !newIds.Contains(em.UserId)).ToList())
            db.ChatMentions.Remove(em);
        var existingIds = existingMentions.Select(em => em.UserId).ToHashSet();
        foreach (var r in newRecipients.Where(r => !existingIds.Contains(r.UserId)))
            db.ChatMentions.Add(new ChatMention { Id = Guid.NewGuid(), MessageId = messageId, UserId = r.UserId, IsAllMention = r.ViaAllOnly, CreatedAt = now, SuppressNotification = r.SuppressNotification });

        await db.SaveChangesAsync(ct);

        return new MessageResultDto(true, await MapWithMentionsAsync(message, userId, ct), null);
    }

    public async Task<MessageResultDto> DeleteMessageAsync(Guid userId, Guid messageId, bool isAdmin, CancellationToken ct)
    {
        var message = await db.ChatMessages
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (message is null || message.IsDeleted)
            return new MessageResultDto(false, null, "message_not_found");

        if (message.SenderId != userId && !isAdmin)
            return new MessageResultDto(false, null, "forbidden");

        message.IsDeleted = true;
        message.DeletedByType = (isAdmin && message.SenderId != userId) ? "admin" : "self";

        await db.SaveChangesAsync(ct);

        return new MessageResultDto(true, MapToMessageDto(message, userId), null);
    }

    public async Task<MessageResultDto> ForwardMessageAsync(Guid userId, Guid messageId, Guid targetRoomId, CancellationToken ct)
    {
        var original = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (original is null || original.IsDeleted)
            return new MessageResultDto(false, null, "message_not_found");

        var isMemberOfTarget = await db.ChatRoomMembers
            .AnyAsync(m => m.RoomId == targetRoomId && m.UserId == userId && m.LeftAt == null, ct);

        if (!isMemberOfTarget)
            return new MessageResultDto(false, null, "forbidden");

        var isMemberOfSource = await db.ChatRoomMembers.AnyAsync(m => m.RoomId == original.RoomId && m.UserId == userId && m.LeftAt == null, ct);
        if (!isMemberOfSource)
            return new MessageResultDto(false, null, "forbidden");

        var now = DateTime.UtcNow;
        // Konten bersih: lepas prefix legacy "[forwarded]" — metadata terusan kini
        // hidup di kolom ForwardedFrom*, tidak lagi di dalam teks pesan.
        var cleanContent = original.Content?.StartsWith("[forwarded]") == true
            ? original.Content["[forwarded]".Length..].TrimStart()
            : original.Content;
        // Asal-usul terusan: bila pesan asal sendiri hasil terusan, pertahankan
        // pengirim ASLI pertama (rantai terusan tidak menimpa asal).
        var originSender = original.ForwardedFromSenderName ?? original.Sender?.Name ?? "Unknown";
        var originMessageId = original.ForwardedFromMessageId ?? original.Id;

        var newMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = targetRoomId,
            SenderId = userId,
            Type = original.Type ?? "text",
            Content = cleanContent,
            IdempotencyKey = Guid.NewGuid(),
            ForwardedFromMessageId = originMessageId,
            ForwardedFromSenderName = originSender,
            CreatedAt = now
        };

        db.ChatMessages.Add(newMessage);

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

        var targetRoom = await db.ChatRooms.FindAsync([targetRoomId], ct);
        if (targetRoom is not null)
            targetRoom.LastMessageAt = now;

        await db.SaveChangesAsync(ct);

        var saved = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .FirstAsync(m => m.Id == newMessage.Id, ct);

        return new MessageResultDto(true, MapToMessageDto(saved, userId), null);
    }

    public async Task<PagedResult<RoomListDto>> SearchRoomsAsync(Guid userId, string query, CancellationToken ct)
    {
        if (query.Length < 2)
            return new PagedResult<RoomListDto>([], 0, 1, 20);

        var pattern = EscapeLikePattern(query);
        var now = DateTime.UtcNow;

        var memberRoomIds = db.ChatRoomMembers
            .Where(m => m.UserId == userId && m.LeftAt == null)
            .Select(m => m.RoomId);

        var archivedIds = db.ChatRoomUserSettings
            .Where(s => s.UserId == userId && s.IsArchived)
            .Select(s => s.RoomId)
            .Concat(HiddenRoomIds(userId));

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
                    otherMember.User.PresenceManualStatus, now, otherMember.User.PresenceIsIdle);
            }

            roomDtos.Add(new RoomListDto(
                r.Id, r.Type, r.Name, recipientNik, recipientName,
                null, r.LastMessageAt == default ? null : r.LastMessageAt,
                0, false, false, false, presenceStatus, false, searchMentionIds.Contains(r.Id), null,
                true, r.AvatarColor, r.AvatarUrl
            ));
        }

        return new PagedResult<RoomListDto>(roomDtos, totalCount, 1, 20);
    }

    public async Task<PagedResult<MessageDto>> SearchMessagesAsync(Guid userId, Guid roomId, string query, int page, int pageSize, CancellationToken ct)
    {
        if (query.Length < 2)
            return new PagedResult<MessageDto>([], 0, page, pageSize);

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

        var dtos = await MapWithMentionsAsync(messages, userId, ct);
        return new PagedResult<MessageDto>(dtos, totalCount, page, pageSize);
    }

    public async Task<PagedResult<MessageDto>> SearchAllMessagesAsync(Guid userId, string query, int page, int pageSize, CancellationToken ct)
    {
        if (query.Length < 2)
            return new PagedResult<MessageDto>([], 0, page, pageSize);

        var pattern = EscapeLikePattern(query);

        // Hanya ruang yang diikuti pengguna (keanggotaan aktif); balasan utas dikecualikan
        // seperti pencarian per-ruang — induk utaslah yang mewakili percakapan.
        var messagesQuery = db.ChatMessages
            .AsNoTracking()
            .Where(m => m.ThreadParentId == null && !m.IsDeleted
                && EF.Functions.ILike(m.Content, pattern)
                && db.ChatRoomMembers.Any(mm => mm.RoomId == m.RoomId && mm.UserId == userId && mm.LeftAt == null));

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

        var dtos = await MapWithMentionsAsync(messages, userId, ct);
        return new PagedResult<MessageDto>(dtos, totalCount, page, pageSize);
    }

    public async Task<PagedResult<MessageDto>> GetUnreadMentionsAsync(Guid userId, int page, int pageSize, CancellationToken ct)
    {
        // Kriteria yang sama dengan penanda HasUnreadMention di daftar ruang:
        // mention milik pengguna, pesan tidak dihapus, bukan kiriman sendiri, dan
        // lebih baru dari tanda baca terakhir ruangnya.
        var mentionsQuery =
            from cm in db.ChatMentions.AsNoTracking()
            join m in db.ChatMessages.AsNoTracking() on cm.MessageId equals m.Id
            join s in db.ChatRoomUserSettings.AsNoTracking().Where(x => x.UserId == userId) on m.RoomId equals s.RoomId into sj
            from s in sj.DefaultIfEmpty()
            where cm.UserId == userId
                  && !m.IsDeleted
                  && m.SenderId != userId
                  && (s == null || s.LastReadMessageAt == null || m.CreatedAt > s.LastReadMessageAt)
            select m;

        var totalCount = await mentionsQuery.Distinct().CountAsync(ct);

        var messages = await mentionsQuery
            .Distinct()
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .Include(m => m.Mentions)
            .Include(m => m.ReplyTo)
                .ThenInclude(r => r!.Sender)
            .ToListAsync(ct);

        var dtos = await MapWithMentionsAsync(messages, userId, ct);
        return new PagedResult<MessageDto>(dtos, totalCount, page, pageSize);
    }

    public async Task<MessageResultDto> SendEmergencyBroadcastAsync(Guid senderId, Guid roomId, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 4000)
            return new MessageResultDto(false, null, content.Length > 4000 ? "content_too_long" : "content_required");

        var role = await GetMemberRoleAsync(senderId, roomId, ct);
        if (role is not ("owner" or "admin"))
            return new MessageResultDto(false, null, "forbidden");

        var room = await db.ChatRooms.FindAsync([roomId], ct);
        if (room is null)
            return new MessageResultDto(false, null, "room_not_found");

        var cutoff = DateTime.UtcNow.AddHours(-24);
        var emergencyInWindow = await db.ChatMessages
            .CountAsync(m => m.RoomId == roomId && m.Type == "emergency" && m.CreatedAt > cutoff, ct);
        if (emergencyInWindow >= 3)
            return new MessageResultDto(false, null, "emergency_rate_limited");

        var now = DateTime.UtcNow;

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

        var archivedSettings = await db.ChatRoomUserSettings
            .Where(s => s.RoomId == roomId && s.IsArchived)
            .ToListAsync(ct);
        foreach (var s in archivedSettings)
            s.IsArchived = false;

        await db.SaveChangesAsync(ct);

        var saved = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .FirstAsync(m => m.Id == message.Id, ct);

        return new MessageResultDto(true, MapToMessageDto(saved, senderId), null);
    }
}
