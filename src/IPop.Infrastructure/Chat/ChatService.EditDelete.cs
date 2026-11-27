using IPop.Application.Chat;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

/// Edit and delete of a message (moved out of ChatService.Messages.cs to keep files small; behaviour unchanged).
public sealed partial class ChatService
{
    public async Task<MessageResultDto> EditMessageAsync(Guid userId, Guid messageId, string newContent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newContent))
            return new MessageResultDto(false, null, "content_required");

        if (newContent.Length > 4000)
            return new MessageResultDto(false, null, "content_too_long");

        // Same graph as the send/history paths: without ReplyTo and Room.Members the edited
        // DTO loses its quote and a group message is mapped as a DM (wrong read status).
        var message = await db.ChatMessages
            .Include(m => m.Sender)
            .Include(m => m.Room).ThenInclude(r => r.Members)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.ReadReceipts)
            .Include(m => m.ReplyTo).ThenInclude(r => r!.Sender)
            .Include(m => m.ReplyTo).ThenInclude(r => r!.Attachments)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (message is null)
            return new MessageResultDto(false, null, "message_not_found");

        if (message.IsDeleted)
            return new MessageResultDto(false, null, "message_deleted");

        if (message.SenderId != userId)
            return new MessageResultDto(false, null, "forbidden");
        // Only plain text is editable: system lines ("X removed Y"), stickers, polls and note
        // cards must not be rewritten into arbitrary text by their sender.
        if (message.Type != "text"
            || message.Content.StartsWith("[poll]", StringComparison.Ordinal)
            || message.Content.StartsWith("[note]", StringComparison.Ordinal))
            return new MessageResultDto(false, null, "not_editable");
        // Someone who left (or was removed from) the room no longer changes what its members read.
        if (!await db.ChatRoomMembers.AsNoTracking().AnyAsync(m => m.RoomId == message.RoomId && m.UserId == userId && m.LeftAt == null, ct))
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
        var newRecipients = await BuildMentionRecipientsAsync(message.RoomId, userId, newContent, ct, excludeMessageId: messageId);
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
        // Membership lines are the group's record: only an admin may remove them, not their "author".
        if (message.Type == "system" && !isAdmin)
            return new MessageResultDto(false, null, "forbidden");

        message.IsDeleted = true;
        message.DeletedByType = (isAdmin && message.SenderId != userId) ? "admin" : "self";
        message.DeletedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        return new MessageResultDto(true, MapToMessageDto(message, userId), null);
    }
}
