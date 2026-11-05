using IPop.Application.Chat;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed partial class ChatService
{
    // ──────────────────────────────────────────────────────────────────────────
    // Delivery and Read Receipts
    // ──────────────────────────────────────────────────────────────────────────

    /// Whether the last RecordDeliveryAsync / RecordReadAsync changed anything. A repeat (another tab, a reconnect,
    /// re-reading the same message) changes nothing, and callers then skip the sender ping / room broadcast.
    public bool LastReceiptChanged { get; private set; }

    public async Task<bool> RecordDeliveryAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        LastReceiptChanged = false;
        var msgRoomId = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Id == messageId).Select(m => m.RoomId).FirstOrDefaultAsync(ct);
        if (msgRoomId == Guid.Empty) return false;
        if (!await db.ChatRoomMembers.AsNoTracking().AnyAsync(m => m.RoomId == msgRoomId && m.UserId == userId && m.LeftAt == null, ct))
            return false;

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
                LastReceiptChanged = true;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
            }
        }
        else if (receipt.DeliveredAt is null)
        {
            receipt.DeliveredAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            LastReceiptChanged = true;
        }
        return true;
    }

    public Task<bool> RecordReadAsync(Guid userId, Guid messageId, CancellationToken ct) => RecordReadAsync(userId, messageId, ct, retried: false);

    private async Task<bool> RecordReadAsync(Guid userId, Guid messageId, CancellationToken ct, bool retried)
    {
        LastReceiptChanged = false;
        var msg = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.RoomId, m.CreatedAt, m.SenderId })
            .FirstOrDefaultAsync(ct);
        if (msg is null) return false;
        if (!await db.ChatRoomMembers.AsNoTracking().AnyAsync(m => m.RoomId == msg.RoomId && m.UserId == userId && m.LeftAt == null, ct))
            return false;
        // Reading up to your own message still advances your read position (clears the badge),
        // but never writes a receipt on it: that would count you as one of its readers.
        var ownMessage = msg.SenderId == userId;

        var receipt = await db.ChatReadReceipts
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId, ct);

        var now = DateTime.UtcNow;
        var readerSettings = await GetOrCreateSettingsAsync(userId, msg.RoomId, ct);
        bool sendReadReceipt = readerSettings.ReadReceiptsEnabled;

        if (ownMessage) { }
        else if (receipt is not null)
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

        // Only receipt rows count as a change worth telling the room (LastReadMessageAt is the reader's own business).
        var receiptsChanged = db.ChangeTracker.Entries<ChatReadReceipt>()
            .Any(e => e.State is EntityState.Added || (e.State is EntityState.Modified
                && (e.Property(r => r.ReadAt).IsModified || e.Property(r => r.DeliveredAt).IsModified)));
        try
        {
            await db.SaveChangesAsync(ct);
            LastReceiptChanged = receiptsChanged;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex) && !retried)
        {
            // A concurrent delivery receipt won the insert race for one of the rows. The whole
            // batch (and LastReadMessageAt) rolled back with it — redo it once against fresh rows.
            db.ChangeTracker.Clear();
            return await RecordReadAsync(userId, messageId, ct, retried: true);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
        }
        return true;
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
        var readers = await db.ChatReadReceipts.AsNoTracking().Where(r => r.MessageId == messageId && r.UserId != msg.SenderId)
            .Join(db.ChatRoomUserSettings.AsNoTracking().Where(s => s.RoomId == msg.RoomId),
                r => r.UserId, s => s.UserId, (r, s) => new { r, s })
            .Where(x => x.s.LastReadMessageAt != null && x.s.LastReadMessageAt >= msg.CreatedAt)
            .Join(db.ChatRoomMembers.AsNoTracking().Where(m => m.RoomId == msg.RoomId && m.LeftAt == null),
                x => x.r.UserId, m => m.UserId, (x, m) => x.r)
            .Include(r => r.User).OrderByDescending(r => r.ReadAt).Take(255)
            .Select(r => new SeenByDto(r.UserId, r.User.Name, r.User.PhotoPath, r.DeliveredAt, r.ReadAt)).ToListAsync(ct);
        var readerIds = readers.Select(r => r.UserId).ToList();
        var pending = await db.ChatRoomMembers.AsNoTracking()
            .Where(m => m.RoomId == msg.RoomId && m.LeftAt == null && m.UserId != msg.SenderId && !readerIds.Contains(m.UserId))
            .OrderBy(m => m.User.Name).Take(255)
            .Select(m => new SeenByDto(m.UserId, m.User.Name, m.User.PhotoPath,
                db.ChatReadReceipts.Where(r => r.MessageId == messageId && r.UserId == m.UserId).Select(r => r.DeliveredAt).FirstOrDefault(),
                null))
            .ToListAsync(ct);
        return new SeenListDto(readers, readers.Count, activeCount, pending);
    }
}
