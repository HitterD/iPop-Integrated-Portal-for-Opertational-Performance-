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

        // Any active member may pin, in DMs and groups alike.
        if (await GetMemberRoleAsync(userId, roomId, ct) is null)
            return new PinnedResultDto(false, "forbidden");

        var alreadyPinned = await db.ChatPinnedMessages
            .AnyAsync(p => p.RoomId == roomId && p.MessageId == messageId, ct);
        if (alreadyPinned)
            return new PinnedResultDto(true, null, roomId);

        var pinCount = await db.ChatPinnedMessages.CountAsync(p => p.RoomId == roomId, ct);
        if (pinCount >= 50)
            return new PinnedResultDto(false, "pin_limit_reached");

        var pin = new ChatPinnedMessage
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            MessageId = messageId,
            PinnedByUserId = userId,
            PinnedAt = DateTime.UtcNow
        };
        db.ChatPinnedMessages.Add(pin);
        var system = await AddPinSystemMessageAsync(userId, roomId, "pinned", ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // A concurrent pin of the same message won: no second pin, no second system row.
            db.Entry(pin).State = EntityState.Detached;
            db.Entry(system).State = EntityState.Detached;
            return new PinnedResultDto(true, null, roomId);
        }
        return new PinnedResultDto(true, null, roomId, Changed: true, SystemMessage: MapToMessageDto(system, Guid.Empty));
    }

    public async Task<PinnedResultDto> UnpinMessageAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        var pinned = await db.ChatPinnedMessages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.MessageId == messageId, ct);
        if (pinned is null)
        {
            // Already unpinned (repeat click, another tab, or another member got there first):
            // a no-op success for members, so nothing is broadcast or written twice.
            var roomOfMessage = await db.ChatMessages.AsNoTracking()
                .Where(m => m.Id == messageId).Select(m => (Guid?)m.RoomId).FirstOrDefaultAsync(ct);
            if (roomOfMessage is null)
                return new PinnedResultDto(false, "message_not_found");
            if (await GetMemberRoleAsync(userId, roomOfMessage.Value, ct) is null)
                return new PinnedResultDto(false, "forbidden");
            return new PinnedResultDto(true, null, roomOfMessage);
        }

        // DMs: any member may unpin. Groups: whoever pinned it, or an owner/admin.
        var role = await GetMemberRoleAsync(userId, pinned.RoomId, ct);
        var isDm = await db.ChatRooms.AsNoTracking().Where(r => r.Id == pinned.RoomId).Select(r => r.Type == "dm").FirstOrDefaultAsync(ct);
        if (role is null || (!isDm && pinned.PinnedByUserId != userId && role is not ("owner" or "admin")))
            return new PinnedResultDto(false, "forbidden");

        var roomId = pinned.RoomId;
        // Delete and record in one transaction; only the request whose delete actually removed
        // the row writes the "unpinned" system message, so concurrent unpins log it once.
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var removed = await db.ChatPinnedMessages.Where(p => p.Id == pinned.Id).ExecuteDeleteAsync(ct);
        if (removed == 0)
            return new PinnedResultDto(true, null, roomId);

        var system = await AddPinSystemMessageAsync(userId, roomId, "unpinned", ct);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return new PinnedResultDto(true, null, roomId, Changed: true, SystemMessage: MapToMessageDto(system, Guid.Empty));
    }

    /// The persisted "X pinned/unpinned a message" timeline row. System rows are excluded from
    /// unread counts and push, and are broadcast on the pin event rather than "newMessage".
    private async Task<ChatMessage> AddPinSystemMessageAsync(Guid userId, Guid roomId, string verb, CancellationToken ct)
    {
        var actor = await db.Users.FindAsync([userId], ct);
        var system = new ChatMessage
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            SenderId = userId,
            Sender = actor!,
            Type = "system",
            Content = $"{actor?.Name ?? "Unknown"} {verb} a message",
            IdempotencyKey = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        };
        db.ChatMessages.Add(system);
        return system;
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
                p.PinnedAt,
                p.PinnedByUserId
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
