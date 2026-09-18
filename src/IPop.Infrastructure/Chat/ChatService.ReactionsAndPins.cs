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
        if (role is not ("owner" or "admin"))
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
}
