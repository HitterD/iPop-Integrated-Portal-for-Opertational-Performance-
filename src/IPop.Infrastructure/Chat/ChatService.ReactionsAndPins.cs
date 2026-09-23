using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed partial class ChatService
{
    // ──────────────────────────────────────────────────────────────────────────
    // Reactions and Pinned Messages
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<ReactionResultDto> ToggleReactionAsync(Guid userId, Guid messageId, string emoji, CancellationToken ct)
    {
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

        var existing = await db.ChatReactions
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId && r.Emoji == emoji, ct);

        if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > 32)
            return new ReactionResultDto(false, null, "invalid_emoji");

        if (existing is not null)
        {
            db.ChatReactions.Remove(existing);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { }

            var countAfterRemove = await db.ChatReactions
                .CountAsync(r => r.MessageId == messageId && r.Emoji == emoji, ct);

            return new ReactionResultDto(true, new ReactionDto(messageId, emoji, countAfterRemove, userId, false), null);
        }
        else
        {
            var distinctEmojiCount = await db.ChatReactions
                .Where(r => r.MessageId == messageId)
                .Select(r => r.Emoji)
                .Distinct()
                .CountAsync(ct);

            if (distinctEmojiCount >= 20)
                return new ReactionResultDto(false, null, "reaction_limit");

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
                var currentCount = await db.ChatReactions
                    .CountAsync(r => r.MessageId == messageId && r.Emoji == emoji, ct);
                return new ReactionResultDto(true, new ReactionDto(messageId, emoji, currentCount, userId, true), null);
            }

            var countAfterAdd = await db.ChatReactions
                .CountAsync(r => r.MessageId == messageId && r.Emoji == emoji, ct);

            return new ReactionResultDto(true, new ReactionDto(messageId, emoji, countAfterAdd, userId, true), null);
        }
    }

    public async Task<PinnedResultDto> PinMessageAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        var message = await db.ChatMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null)
            return new PinnedResultDto(false, "message_not_found");

        var roomId = message.RoomId;

        var role = await GetMemberRoleAsync(userId, roomId, ct);
        // DM members all carry "member" (no owner/admin) — any DM member may pin;
        // in groups pinning stays an owner/admin action.
        var isDm = await db.ChatRooms.AsNoTracking().Where(r => r.Id == roomId).Select(r => r.Type == "dm").FirstOrDefaultAsync(ct);
        if (role is null || (!isDm && role is not ("owner" or "admin")))
            return new PinnedResultDto(false, "forbidden");

        var alreadyPinned = await db.ChatPinnedMessages
            .AnyAsync(p => p.RoomId == roomId && p.MessageId == messageId, ct);
        if (alreadyPinned)
            return new PinnedResultDto(true, null, roomId);

        var pinCount = await db.ChatPinnedMessages.CountAsync(p => p.RoomId == roomId, ct);
        if (pinCount >= 50)
            return new PinnedResultDto(false, "pin_limit_reached");

        db.ChatPinnedMessages.Add(new ChatPinnedMessage
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            MessageId = messageId,
            PinnedByUserId = userId,
            PinnedAt = DateTime.UtcNow
        });

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

        await db.SaveChangesAsync(ct);
        return new PinnedResultDto(true, null, roomId);
    }

    public async Task<PinnedResultDto> UnpinMessageAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        var pinned = await db.ChatPinnedMessages
            .FirstOrDefaultAsync(p => p.MessageId == messageId, ct);
        if (pinned is null)
            return new PinnedResultDto(false, "message_not_found");

        var role = await GetMemberRoleAsync(userId, pinned.RoomId, ct);
        var isDm = await db.ChatRooms.AsNoTracking().Where(r => r.Id == pinned.RoomId).Select(r => r.Type == "dm").FirstOrDefaultAsync(ct);
        if (role is null || (!isDm && role is not ("owner" or "admin")))
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

    // ── Simpanan pribadi (bookmark per pengguna — tidak mengubah tampilan ruang) ──

    public async Task<SavedResultDto> SaveMessageAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        var message = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Id == messageId && !m.IsDeleted)
            .Select(m => new { m.RoomId })
            .FirstOrDefaultAsync(ct);
        if (message is null)
            return new SavedResultDto(false, "message_not_found");

        // Hanya anggota ruang yang boleh menyimpan pesannya.
        if (!await IsMemberAsync(userId, message.RoomId, ct))
            return new SavedResultDto(false, "forbidden");

        var already = await db.ChatSavedMessages
            .AnyAsync(s => s.UserId == userId && s.MessageId == messageId, ct);
        if (already)
            return new SavedResultDto(true, null); // idempotent

        db.ChatSavedMessages.Add(new ChatSavedMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MessageId = messageId,
            SavedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return new SavedResultDto(true, null);
    }

    public async Task<SavedResultDto> UnsaveMessageAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        var saved = await db.ChatSavedMessages
            .FirstOrDefaultAsync(s => s.UserId == userId && s.MessageId == messageId, ct);
        if (saved is null)
            return new SavedResultDto(false, "message_not_found");

        db.ChatSavedMessages.Remove(saved);
        await db.SaveChangesAsync(ct);
        return new SavedResultDto(true, null);
    }

    public async Task<IReadOnlyList<SavedMessageDto>> GetSavedMessagesAsync(Guid userId, CancellationToken ct)
    {
        return await db.ChatSavedMessages
            .AsNoTracking()
            .Where(s => s.UserId == userId && !s.Message.IsDeleted)
            .Include(s => s.Message)
                .ThenInclude(m => m.Sender)
            .OrderByDescending(s => s.SavedAt)
            .Select(s => new SavedMessageDto(
                s.Id,
                s.Message.RoomId,
                s.MessageId,
                s.Message.Sender.Name,
                MentionPlainText.Project(s.Message.Content, 120),
                s.SavedAt
            ))
            .ToListAsync(ct);
    }
}
