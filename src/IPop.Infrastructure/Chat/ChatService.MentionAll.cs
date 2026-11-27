using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

/// @all rules. Any active group member may use it unless an owner/admin switched it off for ordinary
/// members; owners/admins always may. Everyone is capped at 3 @all messages per 10 minutes per group:
/// past the cap the token stays plain text (no mass notification). The cap is counted from the
/// database (ChatMentions), so it holds across API replicas and restarts.
public sealed partial class ChatService
{
    public const int MentionAllLimit = 3;
    public static readonly TimeSpan MentionAllWindow = TimeSpan.FromMinutes(10);

    /// Pure decision: role null = not an active member.
    internal static bool MayMentionAll(string? role, bool allowMembers, int usedInWindow)
        => role is not null
           && (role is "owner" or "admin" || allowMembers)
           && usedInWindow < MentionAllLimit;

    private async Task<bool> MayMentionAllAsync(Guid roomId, Guid senderId, string roomType, Guid? excludeMessageId, CancellationToken ct)
    {
        if (roomType != "group") return false;
        var role = await db.ChatRoomMembers.AsNoTracking()
            .Where(m => m.RoomId == roomId && m.UserId == senderId && m.LeftAt == null)
            .Select(m => m.Role).FirstOrDefaultAsync(ct);
        if (role is null) return false;
        var allow = await db.ChatRooms.AsNoTracking().Where(r => r.Id == roomId)
            .Select(r => r.AllowMemberMentionAll).FirstOrDefaultAsync(ct);
        var used = (await AllMentionTimesAsync(roomId, senderId, excludeMessageId, ct)).Count;
        return MayMentionAll(role, allow, used);
    }

    /// When each of the sender's @all messages in the current window went out (one per message).
    private async Task<List<DateTime>> AllMentionTimesAsync(Guid roomId, Guid senderId, Guid? excludeMessageId, CancellationToken ct)
    {
        var since = DateTime.UtcNow - MentionAllWindow;
        return await db.ChatMentions.AsNoTracking()
            .Where(c => c.IsAllMention && c.CreatedAt > since && c.MessageId != excludeMessageId
                && c.Message.RoomId == roomId && c.Message.SenderId == senderId)
            .GroupBy(c => c.MessageId).Select(g => g.Min(c => c.CreatedAt)).ToListAsync(ct);
    }

    /// The caller's own @all budget in this group, for the composer hint ("2 of 3 left").
    /// Null when the caller is not an active member of a group.
    public async Task<MentionAllQuotaDto?> GetMentionAllQuotaAsync(Guid userId, Guid roomId, CancellationToken ct)
    {
        var room = await db.ChatRooms.AsNoTracking().Where(r => r.Id == roomId)
            .Select(r => new { r.Type, r.AllowMemberMentionAll }).FirstOrDefaultAsync(ct);
        if (room is null || room.Type != "group") return null;
        var role = await db.ChatRoomMembers.AsNoTracking()
            .Where(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null)
            .Select(m => m.Role).FirstOrDefaultAsync(ct);
        if (role is null) return null;
        var times = await AllMentionTimesAsync(roomId, userId, null, ct);
        var allowed = role is "owner" or "admin" || room.AllowMemberMentionAll;
        DateTime? resetsAt = times.Count >= MentionAllLimit ? times.Min() + MentionAllWindow : null;
        return new MentionAllQuotaDto(allowed, Math.Min(times.Count, MentionAllLimit), MentionAllLimit,
            (int)MentionAllWindow.TotalMinutes, resetsAt);
    }

    /// Owner/admin only: lets ordinary members use @all in this group (default on) or not.
    public async Task<RoomSettingsResultDto> SetMemberMentionAllAsync(Guid actorId, Guid roomId, bool allowed, CancellationToken ct)
    {
        var room = await db.ChatRooms.FirstOrDefaultAsync(r => r.Id == roomId, ct);
        if (room is null || room.Type != "group") return new RoomSettingsResultDto(false, "not_a_group");
        if (await GetMemberRoleAsync(actorId, roomId, ct) is not ("owner" or "admin"))
            return new RoomSettingsResultDto(false, "forbidden");
        if (room.AllowMemberMentionAll == allowed) return new RoomSettingsResultDto(true, null);
        room.AllowMemberMentionAll = allowed;
        WriteAudit(roomId, allowed ? "mention_all_on" : "mention_all_off", actorId, null, null, null, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        return new RoomSettingsResultDto(true, null);
    }
}
