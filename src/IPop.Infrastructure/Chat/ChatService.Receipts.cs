using IPop.Application.Chat;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed partial class ChatService
{
    // ──────────────────────────────────────────────────────────────────────────
    // Delivery and Read Receipts
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
        }
    }

    public async Task<IReadOnlyList<SeenByDto>> GetSeenByAsync(Guid messageId, CancellationToken ct)
    {
        return await db.ChatReadReceipts
            .AsNoTracking()
            .Where(r => r.MessageId == messageId && r.ReadAt != null)
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
}
