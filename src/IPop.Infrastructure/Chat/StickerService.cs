using IPop.Application.Chat;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed class StickerService(IPopDbContext db) : IStickerService
{
    public async Task<StickerListDto> ListAsync(Guid userId, CancellationToken ct)
    {
        var mine = await db.UserStickers.AsNoTracking()
            .Where(u => u.UserId == userId)
            .OrderByDescending(u => u.AddedAt)
            .Select(u => new StickerDto(u.StickerId, u.Sticker.IsOfficial, true))
            .ToListAsync(ct);
        var mineIds = mine.Select(s => s.Id).ToHashSet();
        var official = await db.ChatStickers.AsNoTracking()
            .Where(s => s.IsOfficial)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new StickerDto(s.Id, true, false))
            .ToListAsync(ct);
        var usage = await db.StickerUsages.AsNoTracking().Where(u => u.UserId == userId).ToListAsync(ct);
        var recent = usage.Where(u => u.LastUsedAt != null).OrderByDescending(u => u.LastUsedAt)
            .Take(StickerLimits.MaxRecent).Select(u => u.StickerKey).ToList();
        var favorites = usage.Where(u => u.IsFavorite).OrderByDescending(u => u.FavoritedAt)
            .Select(u => u.StickerKey).ToList();
        return new StickerListDto(mine, official.Select(s => s with { IsMine = mineIds.Contains(s.Id) }).ToList(),
            recent, favorites);
    }

    public async Task<bool> SetFavoriteAsync(Guid userId, string key, bool favorite, CancellationToken ct)
    {
        if (!StickerLimits.IsValidKey(key)) return false;
        var now = DateTime.UtcNow;
        var row = await db.StickerUsages.FirstOrDefaultAsync(u => u.UserId == userId && u.StickerKey == key, ct);
        if (row is null)
        {
            if (!favorite) return true;
            db.StickerUsages.Add(new StickerUsage { UserId = userId, StickerKey = key, IsFavorite = true, FavoritedAt = now });
        }
        else if (row.IsFavorite != favorite)
        {
            row.IsFavorite = favorite;
            row.FavoritedAt = favorite ? now : null;
        }
        await SaveIgnoringRaceAsync(ct);
        return true;
    }

    public Task<bool> RecordUseAsync(Guid userId, string key, CancellationToken ct) => RecordUseAsync(db, userId, key, ct);

    /// Shared with the chat send path. Best effort: history is never worth failing a send over.
    internal static async Task<bool> RecordUseAsync(IPopDbContext db, Guid userId, string key, CancellationToken ct)
    {
        if (!StickerLimits.IsValidKey(key)) return false;
        var now = DateTime.UtcNow;
        try
        {
            var updated = await db.StickerUsages.Where(u => u.UserId == userId && u.StickerKey == key)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastUsedAt, now).SetProperty(x => x.UseCount, x => x.UseCount + 1), ct);
            if (updated > 0) return true;
            var row = new StickerUsage { UserId = userId, StickerKey = key, LastUsedAt = now, UseCount = 1 };
            db.StickerUsages.Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.Entry(row).State = EntityState.Detached; }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    private async Task SaveIgnoringRaceAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }
    }

    public async Task<StickerResultDto> CreateAsync(Guid userId, StickerFile file, long sizeBytes, CancellationToken ct)
    {
        if (await db.UserStickers.CountAsync(u => u.UserId == userId, ct) >= StickerLimits.MaxPerUser)
            return new StickerResultDto(false, "limit_reached");
        var now = DateTime.UtcNow;
        var sticker = new ChatSticker
        {
            Id = Guid.NewGuid(), CreatedById = userId, StorageServer = file.Server, StoragePath = file.Path,
            SizeBytes = sizeBytes, CreatedAt = now,
        };
        db.ChatStickers.Add(sticker);
        db.UserStickers.Add(new UserSticker { UserId = userId, StickerId = sticker.Id, AddedAt = now });
        await db.SaveChangesAsync(ct);
        return new StickerResultDto(true, null, new StickerDto(sticker.Id, false, true));
    }

    public async Task<StickerResultDto> SaveAsync(Guid userId, Guid stickerId, CancellationToken ct)
    {
        var sticker = await db.ChatStickers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stickerId, ct);
        if (sticker is null) return new StickerResultDto(false, "not_found");
        var dto = new StickerDto(sticker.Id, sticker.IsOfficial, true);
        if (await db.UserStickers.AnyAsync(u => u.UserId == userId && u.StickerId == stickerId, ct))
            return new StickerResultDto(true, null, dto);
        if (await db.UserStickers.CountAsync(u => u.UserId == userId, ct) >= StickerLimits.MaxPerUser)
            return new StickerResultDto(false, "limit_reached");
        db.UserStickers.Add(new UserSticker { UserId = userId, StickerId = stickerId, AddedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
        return new StickerResultDto(true, null, dto);
    }

    public async Task<bool> RemoveAsync(Guid userId, Guid stickerId, CancellationToken ct) =>
        await db.UserStickers.Where(u => u.UserId == userId && u.StickerId == stickerId).ExecuteDeleteAsync(ct) > 0;

    public async Task<bool> SetOfficialAsync(Guid stickerId, bool official, CancellationToken ct) =>
        await db.ChatStickers.Where(s => s.Id == stickerId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.IsOfficial, official), ct) > 0;

    public Task<StickerFile?> GetFileAsync(Guid stickerId, CancellationToken ct) =>
        db.ChatStickers.AsNoTracking().Where(s => s.Id == stickerId)
            .Select(s => new StickerFile(s.StorageServer, s.StoragePath)).FirstOrDefaultAsync(ct);
}
