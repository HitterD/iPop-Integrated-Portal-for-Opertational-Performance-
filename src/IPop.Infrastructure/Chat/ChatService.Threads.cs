using IPop.Application.Chat;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed partial class ChatService
{
    // ──────────────────────────────────────────────────────────────────────────
    // Thread operations
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<MessageResultDto> SendThreadReplyAsync(Guid senderId, Guid parentMessageId, SendMessageRequest req, CancellationToken ct)
    {
        if (req.Content.Length > 4000)
            return new MessageResultDto(false, null, "content_too_long");

        var parent = await db.ChatMessages
            .FirstOrDefaultAsync(m => m.Id == parentMessageId, ct);

        if (parent is null)
            return new MessageResultDto(false, null, "message_not_found");

        var isMember = await db.ChatRoomMembers
            .AnyAsync(m => m.RoomId == parent.RoomId && m.UserId == senderId && m.LeftAt == null, ct);

        if (!isMember)
            return new MessageResultDto(false, null, "room_not_found");
        if (!ChatLimits.SenderTypes.Contains(req.Type ?? "text"))
            return new MessageResultDto(false, null, "invalid_type");

        var threadProjErr = await ValidateProjectTaskMentionsAsync(senderId, req.Content, ct);
        if (threadProjErr != null)
            return new MessageResultDto(false, null, threadProjErr);

        if (parent.ThreadReplyCount >= ChatLimits.ThreadReplies)
            return new MessageResultDto(false, null, "thread_full");

        if (req.ReplyToId is not null)
        {
            var replyOk2 = await db.ChatMessages.AsNoTracking().AnyAsync(m => m.Id == req.ReplyToId.Value && m.RoomId == parent.RoomId && !m.IsDeleted, ct);
            if (!replyOk2) return new MessageResultDto(false, null, "message_not_found");
        }

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
            LinkPreviewOff = req.LinkPreviewOff,
            CreatedAt = now
        };

        db.ChatMessages.Add(reply);

        if (req.AttachmentIds is { Count: > 0 })
        {
            var roomD = parent.RoomId.ToString("D"); var roomN = parent.RoomId.ToString("N");
            var orphans = await db.ChatAttachments
                .Where(a => req.AttachmentIds.Contains(a.Id) && a.MessageId == null
                    // Only files uploaded into this room: an id from another room must not be pulled in.
                    && (a.SynologyPath.Contains(roomD) || a.SynologyPath.Contains(roomN)))
                .ToListAsync(ct);
            foreach (var a in orphans)
                a.MessageId = reply.Id;
        }

        parent.ThreadReplyCount++;
        parent.LastThreadReplyAt = now;

        await db.SaveChangesAsync(ct);
        if (reply.Type == "sticker") await StickerService.RecordUseAsync(db, senderId, reply.Content, ct);

        var saved = await db.ChatMessages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .Include(m => m.ReplyTo)
                .ThenInclude(r => r!.Sender)
            .FirstAsync(m => m.Id == reply.Id, ct);

        return new MessageResultDto(true, MapToMessageDto(saved, senderId), null);
    }

    public async Task<PagedResult<MessageDto>> GetThreadRepliesAsync(Guid parentMessageId, Guid userId, int page, int pageSize, CancellationToken ct)
    {
        var parent = await db.ChatMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == parentMessageId, ct);

        if (parent is null)
            return new PagedResult<MessageDto>([], 0, page, pageSize);

        var isMember = await db.ChatRoomMembers.AsNoTracking()
            .AnyAsync(m => m.RoomId == parent.RoomId && m.UserId == userId && m.LeftAt == null, ct);
        if (!isMember)
            return new PagedResult<MessageDto>([], 0, page, pageSize);

        var query = db.ChatMessages
            .AsNoTracking()
            .Where(m => m.ThreadParentId == parentMessageId && !m.IsDeleted);

        var totalCount = await query.CountAsync(ct);

        var replies = await query
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

        var dtos = await MapWithMentionsAsync(replies, userId, ct);
        return new PagedResult<MessageDto>(dtos, totalCount, page, pageSize);
    }
}
