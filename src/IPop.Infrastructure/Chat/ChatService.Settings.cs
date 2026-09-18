using IPop.Application.Chat;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed partial class ChatService
{
    // ──────────────────────────────────────────────────────────────────────────
    // Room Settings, Mute, Archive, Identity
    // ──────────────────────────────────────────────────────────────────────────

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
        if (changed)
        {
            var now = DateTime.UtcNow;
            var actor = await db.Users.FindAsync([actorId], ct);
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(), RoomId = roomId, SenderId = actorId, Type = "system",
                Content = $"{actor?.Name ?? "Admin"} updated the group info", IdempotencyKey = Guid.NewGuid(), CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
        }
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

    public async Task<PagedResult<RoomListDto>> GetArchivedRoomsAsyncImpl(Guid userId, int page, int pageSize, CancellationToken ct)
    {
        var archivedIds = db.ChatRoomUserSettings.Where(s => s.UserId == userId && s.IsArchived).Select(s => s.RoomId);
        var q = db.ChatRooms.AsNoTracking().Where(r => archivedIds.Contains(r.Id) && r.IsActive);
        var total = await q.CountAsync(ct);
        var rooms = await q.OrderByDescending(r => r.LastMessageAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var dtos = rooms.Select(r => new RoomListDto(r.Id, r.Type, r.Name, null, null, null, r.LastMessageAt == default ? null : r.LastMessageAt, 0, false, true, true, null, false, false, null)).ToList();
        return new PagedResult<RoomListDto>(dtos, total, page, pageSize);
    }
}
