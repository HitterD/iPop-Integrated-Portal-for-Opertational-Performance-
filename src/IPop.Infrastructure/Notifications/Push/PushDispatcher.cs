using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPop.Application.Calls;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notifications.Push;

/// What the service worker (wwwroot/sw.js) receives. Kind drives its behaviour:
/// "notif" | "chat" (grouped per room tag) | "call" (sticky, Jawab/Tolak) | "close" (dismiss Tag).
public sealed record PushPayload(
    string Kind, string Title, string Body, string? Url, string Tag,
    bool RequireInteraction = false,
    Guid? CallId = null,
    string? ActionToken = null,
    string? ApiBase = null);

/// Sends Web Push to every browser a user subscribed, deleting subscriptions the push
/// service reports gone (404/410). Runs inside <see cref="PushWorker"/>, never on a request.
public sealed class PushDispatcher(IPopDbContext db, IWebPushTransport transport, IDataProtectionProvider dataProtection,
    ILogger<PushDispatcher> log)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// Toast actions from the service worker carry this instead of a JWT (it cannot read one).
    public const string CallActionPurpose = "push.call-action";
    public static readonly TimeSpan CallActionLifetime = TimeSpan.FromMinutes(2);

    /// (callId, userId) pairs whose ring toast is showing — so a ring is sent once and closed once.
    /// In-process: after a restart an open ring toast is not closed, but its push TTL (the ring
    /// timeout) keeps it from arriving late.
    private static readonly ConcurrentDictionary<(Guid CallId, Guid UserId), byte> Rung = new();

    public async Task SendAsync(IReadOnlyCollection<Guid> userIds, Func<Guid, PushPayload?> payloadFor,
        bool urgent, TimeSpan ttl, CancellationToken ct)
    {
        if (!transport.IsConfigured || userIds.Count == 0) return;
        var subs = await db.PushSubscriptions.Where(s => userIds.Contains(s.UserId)).ToListAsync(ct);
        var gone = new List<PushSubscription>();
        var sent = 0;
        string? kind = null;
        foreach (var sub in subs)
        {
            var payload = payloadFor(sub.UserId);
            if (payload is null) continue;
            kind = payload.Kind;
            var json = JsonSerializer.Serialize(payload with { ApiBase = sub.ApiBase }, Json);
            var result = await transport.SendAsync(sub, json, ttl, urgent, TopicOf(payload.Tag), ct);
            if (result == PushSendResult.Gone) gone.Add(sub);
            else if (result == PushSendResult.Sent) sent++;
        }
        if (kind is not null) log.LogDebug("Web push {Kind}: {Sent}/{Total} delivered to the push service.", kind, sent, subs.Count);
        if (gone.Count > 0)
        {
            db.PushSubscriptions.RemoveRange(gone);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Removed {Count} expired push subscription(s).", gone.Count);
        }
    }

    public Task SendAsync(IReadOnlyCollection<Guid> userIds, PushPayload payload, bool urgent, CancellationToken ct) =>
        SendAsync(userIds, _ => payload, urgent, TimeSpan.FromHours(12), ct);

    // ── Chat ──────────────────────────────────────────────────────────────────

    /// A new chat message → a toast for every other member of the room, except where the
    /// room is muted/archived, the sender is blocked, chat is a muted type or quiet hours
    /// apply, or the member is @-mentioned (the mention notification toasts instead).
    public async Task NotifyChatMessageAsync(Guid messageId, CancellationToken ct)
    {
        var msg = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.Id, m.RoomId, m.SenderId, SenderName = m.Sender.Name, m.Type, m.Content, m.IsDeleted,
                RoomType = m.Room.Type, RoomName = m.Room.Name, Files = m.Attachments.Count })
            .FirstOrDefaultAsync(ct);
        if (msg is null || msg.IsDeleted || msg.Type == "system") return;
        var emergency = msg.Type == "emergency";

        var now = DateTime.UtcNow;
        var members = await db.ChatRoomMembers.AsNoTracking()
            .Where(m => m.RoomId == msg.RoomId && m.LeftAt == null && m.UserId != msg.SenderId && m.User.IsActive)
            .Select(m => m.UserId).ToListAsync(ct);
        if (members.Count == 0) return;

        var silencedRoom = await db.ChatRoomUserSettings.AsNoTracking()
            .Where(s => s.RoomId == msg.RoomId && members.Contains(s.UserId)
                && (s.IsArchived || (s.IsMuted && (s.MuteExpiresAt == null || s.MuteExpiresAt > now))))
            .Select(s => s.UserId).ToListAsync(ct);
        var blockers = await db.UserBlocks.AsNoTracking()
            .Where(b => b.BlockedUserId == msg.SenderId && members.Contains(b.UserId))
            .Select(b => b.UserId).ToListAsync(ct);
        var mentioned = await db.ChatMentions.AsNoTracking()
            .Where(m => m.MessageId == msg.Id && !m.SuppressNotification)
            .Select(m => m.UserId).ToListAsync(ct);
        var silenced = await SilencedAsync(db, members, "chat.message", "normal", now, ct);

        // Emergency broadcasts ignore room mutes, quiet hours and muted types.
        var recipients = emergency
            ? members.Except(blockers).ToList()
            : members.Except(silencedRoom).Except(blockers).Except(mentioned).Except(silenced).ToList();
        if (recipients.Count == 0) return;

        var previewOff = emergency ? [] : await PreviewOffAsync(recipients, ct);
        var title = emergency ? $"🚨 DARURAT · {msg.RoomName ?? msg.SenderName}"
            : msg.RoomType == "group" ? $"{msg.SenderName} · {msg.RoomName ?? "Grup"}" : msg.SenderName;
        var preview = msg.Type == "sticker" ? "🎨 Stiker" : ChatPreview(msg.Content, msg.Files);
        var url = $"/chat/{msg.RoomId}";
        var tag = $"chat:{msg.RoomId}";

        await SendAsync(recipients,
            uid => new PushPayload("chat", title, previewOff.Contains(uid) ? "Pesan baru" : preview, url,
                emergency ? $"emergency:{msg.Id:N}" : tag, RequireInteraction: emergency),
            urgent: emergency, TimeSpan.FromHours(emergency ? 1 : 12), ct);
    }

    /// One line for the toast body: mention markup flattened, cards/attachments named.
    internal static string ChatPreview(string content, int attachments)
    {
        if (content.StartsWith("[poll]")) return "📊 Polling";
        if (content.StartsWith("[note]")) return "📝 Membagikan catatan";
        var text = IPop.Application.Chat.Mentions.MentionPlainText.Project(content, 120).Trim();
        if (text.Length > 0) return text;
        return attachments switch { 0 => "Pesan baru", 1 => "📎 Lampiran", _ => $"📎 {attachments} lampiran" };
    }

    // ── Gateway notifications (mentions, emergencies, module events) ──────────

    /// Recipients were already filtered by the caller; chat bodies honour the preview switch.
    public async Task NotifyRequestAsync(IReadOnlyCollection<Guid> userIds, string eventType, string priority,
        string title, string body, string? url, Guid requestId, CancellationToken ct)
    {
        var isChat = NotificationGatewayService.ClassifyEventType(eventType) == "chat";
        var emergency = eventType == "chat.emergency";
        var previewOff = isChat && !emergency ? await PreviewOffAsync(userIds, ct) : [];
        // Chat notifications share the room's tag so they replace that room's message toast.
        var roomId = isChat ? ChatRoomOf(url) : null;
        var tag = roomId is { } r ? $"chat:{r}" : $"notif:{requestId:N}";
        await SendAsync(userIds,
            uid => new PushPayload(isChat ? "chat" : "notif", title,
                previewOff.Contains(uid) ? "Pesan baru" : body, url ?? "/notifications", tag,
                RequireInteraction: emergency),
            urgent: priority == "high", TimeSpan.FromHours(emergency ? 1 : 24), ct);
    }

    internal static Guid? ChatRoomOf(string? url) =>
        url is not null && url.StartsWith("/chat/") && url.Length >= 42 && Guid.TryParse(url.AsSpan(6, 36), out var id) ? id : null;

    // ── Calls ─────────────────────────────────────────────────────────────────

    /// Mirrors every call state broadcast: invited users of a live call get one sticky ring
    /// toast; once they answer, decline, or the call ends, the toast is closed on all devices.
    public async Task SyncCallAsync(CallRoomDto room, CancellationToken ct)
    {
        var live = room.Status == CallStatus.Ringing || (room.Kind == CallKind.Group && room.Status == CallStatus.Active);
        var invited = live
            ? room.Participants.Where(p => p.Status == CallParticipantStatus.Invited && p.UserId != room.CreatorId)
                .Select(p => p.UserId).ToHashSet()
            : [];
        var ringNow = invited.Where(u => Rung.TryAdd((room.Id, u), 0)).ToList();
        var closeNow = Rung.Keys.Where(k => k.CallId == room.Id && !invited.Contains(k.UserId)).Select(k => k.UserId).ToList();
        foreach (var u in closeNow) Rung.TryRemove((room.Id, u), out _);

        var tag = $"call:{room.Id}";
        if (ringNow.Count > 0)
        {
            var caller = room.Participants.FirstOrDefault(p => p.UserId == room.CreatorId)?.Name ?? "Seseorang";
            var isGroup = room.Kind == CallKind.Group;
            var title = isGroup ? $"Panggilan grup · {room.Title ?? "Grup"}" : $"Panggilan masuk · {caller}";
            var body = isGroup ? $"{caller} memulai panggilan grup" : "Panggilan suara iPop";
            var protector = dataProtection.CreateProtector(CallActionPurpose).ToTimeLimitedDataProtector();
            await SendAsync(ringNow,
                uid => new PushPayload("call", title, body, "/calls", tag, RequireInteraction: true, CallId: room.Id,
                    ActionToken: protector.Protect($"{room.Id:N}|{uid:N}", CallActionLifetime)),
                urgent: true, IPop.Infrastructure.Calls.CallService.RingTimeout, ct);
        }
        if (closeNow.Count > 0)
            await SendAsync(closeNow, new PushPayload("close", "", "", null, tag), urgent: true, ct);
    }

    /// Reads a toast action token back into (callId, userId); null when forged or expired.
    public static (Guid CallId, Guid UserId)? ReadCallActionToken(IDataProtectionProvider dp, string token)
    {
        try
        {
            var raw = dp.CreateProtector(CallActionPurpose).ToTimeLimitedDataProtector().Unprotect(token);
            var parts = raw.Split('|');
            return parts.Length == 2 && Guid.TryParse(parts[0], out var c) && Guid.TryParse(parts[1], out var u) ? (c, u) : null;
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    // ── Preferences ───────────────────────────────────────────────────────────

    /// Users whose preferences silence this event now: a muted type, or quiet hours unless
    /// the event is high priority and they let important ones through.
    internal static async Task<HashSet<Guid>> SilencedAsync(IPopDbContext db, IReadOnlyCollection<Guid> userIds,
        string eventType, string priority, DateTime now, CancellationToken ct)
    {
        var prefs = await db.NotificationPreferences.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId) && (p.QuietHoursEnabled || p.MutedTypes != ""))
            .Select(p => new { Pref = p, p.User.TimeZoneId })
            .ToListAsync(ct);
        return prefs.Where(x => IsSilenced(x.Pref, x.TimeZoneId, eventType, priority, now))
            .Select(x => x.Pref.UserId).ToHashSet();
    }

    internal static bool IsSilenced(NotificationPreference pref, string? timeZoneId, string eventType, string priority, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(pref.MutedTypes))
        {
            var muted = pref.MutedTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (muted.Contains(NotificationGatewayService.ClassifyEventType(eventType), StringComparer.OrdinalIgnoreCase)
                || muted.Contains(eventType, StringComparer.OrdinalIgnoreCase))
                return true;
        }
        if (pref.QuietHoursEnabled && !(priority == "high" && pref.AllowImportant))
        {
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId ?? "SE Asia Standard Time"); }
            catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
            if (NotificationGatewayService.IsInQuietHours(pref, now, tz)) return true;
        }
        return false;
    }

    private async Task<HashSet<Guid>> PreviewOffAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        (await db.NotificationPreferences.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId) && !p.PushShowPreview)
            .Select(p => p.UserId).ToListAsync(ct)).ToHashSet();

    /// RFC 8030 topic (≤32 URL-safe chars): a newer message replaces a still-queued one with the same tag.
    private static string? TopicOf(string tag)
    {
        var clean = new string(tag.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return clean.Length == 0 ? null : clean.Length <= 32 ? clean : clean[..32];
    }
}
