using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed partial class ChatService : IChatService
{
    private readonly IPopDbContext db;
    private readonly INotificationGatewayService? notificationGateway;

    public ChatService(IPopDbContext db, INotificationGatewayService? notificationGateway = null)
    {
        this.db = db;
        this.notificationGateway = notificationGateway;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Interface explicit mapping
    // ──────────────────────────────────────────────────────────────────────────

    Task<RoomSettingsResultDto> IChatService.UpdateGroupIdentityAsync(Guid actorId, Guid roomId, string name, string? description, CancellationToken ct)
        => UpdateGroupIdentityAsyncImpl(actorId, roomId, name, description, ct);

    Task<PagedResult<MembershipAuditDto>> IChatService.GetMembershipAuditAsync(Guid callerId, Guid roomId, int page, int pageSize, CancellationToken ct)
        => GetMembershipAuditAsyncImpl(callerId, roomId, page, pageSize, ct);

    Task<SeenListDto> IChatService.GetSeenDetailAsync(Guid callerId, Guid messageId, CancellationToken ct)
        => GetSeenDetailAsyncImpl(callerId, messageId, ct);

    Task<RoomSettingsResultDto> IChatService.PinRoomAsync(Guid userId, Guid roomId, CancellationToken ct)
        => PinRoomAsyncImpl(userId, roomId, ct);

    Task<RoomSettingsResultDto> IChatService.UnpinRoomAsync(Guid userId, Guid roomId, CancellationToken ct)
        => UnpinRoomAsyncImpl(userId, roomId, ct);

    Task<PagedResult<RoomListDto>> IChatService.GetArchivedRoomsAsync(Guid userId, int page, int pageSize, CancellationToken ct)
        => GetArchivedRoomsAsyncImpl(userId, page, pageSize, ct);

    // ──────────────────────────────────────────────────────────────────────────
    // Private Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private MessageDto MapToMessageDto(ChatMessage m, Guid currentUserId)
    {
        var content = m.IsDeleted
            ? m.DeletedByType == "admin"
                ? "This message was deleted by an admin"
                : "This message was deleted"
            : m.Content;

        ReplyPreviewDto? replyPreview = null;
        if (m.ReplyTo is not null)
        {
            var previewContent = m.ReplyTo.IsDeleted
                ? "Original message was deleted"
                : Truncate(m.ReplyTo.Content, 150);
            replyPreview = new ReplyPreviewDto(m.ReplyTo.Sender?.Name ?? "Unknown", previewContent);
        }

        var reactions = m.Reactions?
            .GroupBy(r => r.Emoji)
            .Select(g => new ReactionGroupDto(
                g.Key,
                g.Count(),
                g.Any(r => r.UserId == currentUserId)
            ))
            .ToList() as IReadOnlyList<ReactionGroupDto> ?? [];

        var attachments = m.Attachments?
            .Select(a => new AttachmentDto(
                a.Id,
                a.FileName,
                a.FileSize,
                a.MimeType,
                a.ThumbnailPath != null ? $"/api/chat/attachments/{a.Id}/thumbnail" : null
            ))
            .ToList() as IReadOnlyList<AttachmentDto> ?? [];

        string? deliveryStatus = null;
        if (m.SenderId == currentUserId)
        {
            var receipts = m.ReadReceipts ?? Enumerable.Empty<ChatReadReceipt>();
            var others = receipts.Where(r => r.UserId != currentUserId).ToList();
            var isGroup = m.Room?.Type == "group";
            if (!isGroup)
            {
                if (others.Any(r => r.ReadAt != null))
                    deliveryStatus = "read";
                else if (others.Any(r => r.DeliveredAt != null))
                    deliveryStatus = "delivered";
                else
                    deliveryStatus = "sent";
            }
            else
            {
                var activeOtherMembers = m.Room?.Members?.Count(mem => mem.LeftAt == null && mem.UserId != currentUserId) ?? 0;
                var readCount = others.Count(r => r.ReadAt != null);
                if (activeOtherMembers > 0 && readCount >= activeOtherMembers)
                    deliveryStatus = "read";
                else if (others.Any(r => r.DeliveredAt != null || r.ReadAt != null))
                    deliveryStatus = "delivered";
                else
                    deliveryStatus = "sent";
            }
        }

        return new MessageDto(
            m.Id, m.RoomId, m.SenderId,
            m.Sender?.Name ?? "Unknown", m.Sender?.Nik ?? "Unknown",
            m.Type, content, m.CreatedAt, m.EditedAt, m.IsDeleted,
            m.DeletedByType, m.ReplyToId, replyPreview,
            m.ThreadParentId, m.ThreadReplyCount, m.LastThreadReplyAt,
            attachments, reactions,
            [], false, m.IdempotencyKey,
            deliveryStatus);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        => ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<string?> GetMemberRoleAsync(Guid userId, Guid roomId, CancellationToken ct)
        => await db.ChatRoomMembers.AsNoTracking()
            .Where(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null)
            .Select(m => m.Role).FirstOrDefaultAsync(ct);

    private Task<bool> IsMemberAsync(Guid userId, Guid roomId, CancellationToken ct)
        => db.ChatRoomMembers.AsNoTracking()
            .AnyAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct);

    private async Task<ChatRoomUserSettings> GetOrCreateSettingsAsync(Guid userId, Guid roomId, CancellationToken ct)
    {
        var local = db.ChatRoomUserSettings.Local.FirstOrDefault(s => s.UserId == userId && s.RoomId == roomId);
        if (local is not null) return local;

        var settings = await db.ChatRoomUserSettings.FirstOrDefaultAsync(s => s.UserId == userId && s.RoomId == roomId, ct);
        if (settings is null)
        {
            settings = new ChatRoomUserSettings { UserId = userId, RoomId = roomId };
            db.ChatRoomUserSettings.Add(settings);
        }
        return settings;
    }

    private async Task<string> GetUserNameAsync(Guid userId, CancellationToken ct)
        => await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "Unknown";

    private void WriteAudit(Guid roomId, string action, Guid actorId, Guid? targetUserId, string? oldValue, string? newValue, DateTime at)
    {
        db.ChatMembershipAudits.Add(new ChatMembershipAudit
        {
            Id = Guid.NewGuid(), RoomId = roomId, Action = action,
            ActorId = actorId, TargetUserId = targetUserId,
            OldValue = oldValue, NewValue = newValue, CreatedAt = at,
        });
    }

    private async Task<IReadOnlyList<MentionRecipient>> BuildMentionRecipientsAsync(Guid roomId, Guid senderId, string content, CancellationToken ct)
    {
        var segs = MentionParser.Parse(content);
        var roomType = await db.ChatRooms.AsNoTracking().Where(r => r.Id == roomId).Select(r => r.Type).FirstOrDefaultAsync(ct) ?? "group";
        var members = await db.ChatRoomMembers.AsNoTracking().Where(m => m.RoomId == roomId).ToListAsync(ct);
        var dict = members.ToDictionary(m => m.UserId, m => m.LeftAt == null);
        return MentionResolver.Resolve(new MentionResolutionInput(senderId, roomType, segs, dict));
    }

    private async Task<string?> ValidateProjectTaskMentionsAsync(Guid senderId, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var segs = MentionParser.Parse(content);
        var projectIds = new HashSet<Guid>();
        var taskIds = new HashSet<Guid>();

        foreach (var s in segs)
        {
            if (s is MentionSegment.Project p) projectIds.Add(p.ProjectId);
            else if (s is MentionSegment.Task t) taskIds.Add(t.TaskId);
        }

        if (projectIds.Count == 0 && taskIds.Count == 0) return null;

        if (taskIds.Count > 0)
        {
            var taskProjectIds = await db.ProjectTasks.AsNoTracking()
                .Where(t => taskIds.Contains(t.Id))
                .Select(t => t.ProjectId)
                .Distinct()
                .ToListAsync(ct);

            foreach (var pid in taskProjectIds)
                if (pid != Guid.Empty) projectIds.Add(pid);
        }

        if (projectIds.Count == 0) return null;

        var memberOf = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == senderId && projectIds.Contains(m.ProjectId))
            .Select(m => m.ProjectId)
            .ToHashSetAsync(ct);

        foreach (var pid in projectIds)
        {
            if (!memberOf.Contains(pid))
                return "not_project_member";
        }

        return null;
    }

    private static string EscapeLikePattern(string query)
        => $"%{query.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
}
