using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPop.Application.Calls;
using IPop.Core.Entities;
using IPop.Core.Presence;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notifications.Push;

/// What the service worker (wwwroot/sw.js) receives. Kind drives its behaviour:
/// "notif" | "chat" (grouped per room tag) | "call" (sticky, Answer/Decline) | "close" (dismiss Tag) | "test".
/// DeliveryId/JobId identify the outbox rows (diagnostics); Acct is the account binding the
/// service worker compares with the signed-in account before showing anything.
public sealed record PushPayload(
    string Kind, string Title, string Body, string? Url, string Tag,
    bool RequireInteraction = false,
    Guid? CallId = null,
    string? ActionToken = null,
    string? ApiBase = null,
    string? DeliveryId = null,
    string? JobId = null,
    string? Acct = null,
    string? Sender = null);

/// Web Push from the durable outbox. A <see cref="PushJob"/> is expanded once into one
/// <see cref="PushDelivery"/> per browser; each delivery is sent and retried on its own, and the
/// toast is rebuilt from current data on every attempt so a retry never shows something the
/// user may no longer see (left room, muted, preview off, call already answered). Runs in
/// <see cref="PushWorker"/>, never on a request.
public sealed class PushDispatcher(IPopDbContext db, IWebPushTransport transport, IDataProtectionProvider dataProtection,
    ILogger<PushDispatcher> log, IDesktopToastSink? desktop = null, INativePushTransport? native = null)
{
    private bool NativeOn => native is { IsConfigured: true };
    private bool AnyOn => transport.IsConfigured || NativeOn;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// Toast actions from the service worker carry this instead of a JWT (it cannot read one).
    public const string CallActionPurpose = "push.call-action";
    public static readonly TimeSpan CallActionLifetime = TimeSpan.FromMinutes(2);

    /// A claimed job/delivery is retaken by any worker once this passes (crash mid-send).
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const int Batch = 50;

    public static class Status
    {
        public const string Queued = "queued", Retrying = "retrying", Sending = "sending", Accepted = "accepted",
            Gone = "gone", AuthError = "auth-error", Rejected = "rejected", Expired = "expired",
            Suppressed = "suppressed", Failed = "failed";
        public static bool IsPending(string s) => s is Queued or Retrying or Sending;
    }

    // ── Worker entry points ───────────────────────────────────────────────────

    /// One pass: expand due jobs, then send due deliveries. Returns how much work was done.
    public async Task<int> ProcessAsync(DateTime now, CancellationToken ct)
    {
        var n = await ExpandDueJobsAsync(now, ct);
        n += await SendDueAsync(now, ct);
        return n;
    }

    public async Task<int> ExpandDueJobsAsync(DateTime now, CancellationToken ct)
    {
        var due = await db.PushJobs.AsNoTracking()
            .Where(j => j.ExpandedAt == null && j.NextAttemptAt <= now)
            .OrderBy(j => j.NextAttemptAt).Take(Batch)
            .Select(j => new { j.Id, j.NextAttemptAt })
            .ToListAsync(ct);
        var done = 0;
        foreach (var d in due)
        {
            // Claim: whoever moves NextAttemptAt first owns the job until the lease passes.
            var claimed = await db.PushJobs.Where(j => j.Id == d.Id && j.ExpandedAt == null && j.NextAttemptAt == d.NextAttemptAt)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.NextAttemptAt, now + Lease).SetProperty(j => j.Attempts, j => j.Attempts + 1), ct);
            if (claimed == 0) continue;
            var job = await db.PushJobs.FirstAsync(j => j.Id == d.Id, ct);
            try
            {
                await ExpandAsync(job, now, ct);
                job.ExpandedAt = now;
                job.LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                db.ChangeTracker.Clear();
                job = await db.PushJobs.FirstAsync(j => j.Id == d.Id, ct);
                job.LastError = job.Attempts >= PushJob.MaxExpandAttempts ? "max-attempts" : "expand-error";
                if (job.Attempts >= PushJob.MaxExpandAttempts)
                {
                    job.ExpandedAt = now;
                    log.LogError(ex, "Web push job {JobId} ({Kind}) dropped after {Attempts} expansion attempts.", job.Id, job.Kind, job.Attempts);
                }
                else job.NextAttemptAt = now.AddSeconds(Math.Min(300, 5 * Math.Pow(2, job.Attempts)));
                log.LogWarning("Web push job {JobId} ({Kind}) expansion failed: {Error}.", job.Id, job.Kind, ex.GetType().Name);
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            done++;
        }
        return done;
    }

    /// Creates the per-browser rows. Calls diff against earlier rings so each ring/close is queued once.
    internal async Task ExpandAsync(PushJob job, DateTime now, CancellationToken ct)
    {
        // iPop Notifier (desktop app): chat messages have no gateway notification, so their toast goes to the app here,
        // with the same mute/block/quiet-hours audience as the browser toast. Independent of Web Push being configured.
        var desktopUsers = await DesktopUsersAsync(job, now, ct);
        if (!AnyOn)
        {
            job.LastError = "push-disabled";
            log.LogWarning("Web push is not configured (VAPID); {Kind} job {JobId} produced no Windows toast.", job.Kind, job.Id);
            return;
        }
        if (job.Kind == "call") { await ExpandCallAsync(job, now, ct); return; }

        var audience = await AudienceAsync(job, null, now, ct);
        if (audience is null || audience.Users.Count == 0 || audience.ExpiresAt <= now) return;
        var subs = await db.PushSubscriptions.AsNoTracking().Where(s => audience.Users.Contains(s.UserId))
            .Select(s => new { s.Id, s.UserId, s.Endpoint, s.Label }).ToListAsync(ct);
        var nativeUsers = subs.Where(s => NativePush.IsNative(s.Endpoint)).Select(s => s.UserId).ToHashSet();
        foreach (var s in subs)
        {
            if (!Deliverable(s.Endpoint)) continue;
            // The app shows this on the user's Windows PC already; a browser toast there would be the same thing twice.
            if (desktopUsers.Contains(s.UserId) && IsWindowsBrowser(s.Label)) continue;
            // The native Android app owns this user's phone notifications; the phone browser would duplicate them
            // and open the browser on tap.
            if (nativeUsers.Contains(s.UserId) && IsAndroidBrowser(s.Endpoint, s.Label)) continue;
            db.PushDeliveries.Add(NewDelivery(job, s.Id, s.UserId, s.Endpoint, audience.Kind, null, audience.ExpiresAt, now));
        }
    }

    /// Users with the desktop app online. For chat jobs they also get the toast through <see cref="IDesktopToastSink"/>;
    /// for gateway notifications the app already receives them over the notification hub.
    private async Task<HashSet<Guid>> DesktopUsersAsync(PushJob job, DateTime now, CancellationToken ct)
    {
        if (job.Kind is not ("chat" or "notif")) return [];
        var audience = await AudienceAsync(job, null, now, ct);
        if (audience is null || audience.Users.Count == 0 || audience.ExpiresAt <= now) return [];
        var since = now - NotifierDevice.OnlineWindow;
        var users = audience.Users;
        var online = (await db.NotifierDevices.AsNoTracking().Where(d => users.Contains(d.UserId) && d.LastSeenAt >= since)
            .Select(d => d.UserId).Distinct().ToListAsync(ct)).ToHashSet();
        if (job.Kind == "chat" && desktop is not null)
            foreach (var uid in online)
                if (audience.PayloadFor(uid) is { } payload)
                {
                    try { await desktop.SendAsync(uid, payload, ct); }
                    catch (Exception ex) { log.LogWarning("Desktop toast for job {JobId} failed ({Error}).", job.Id, ex.GetType().Name); }
                }
        return online;
    }

    internal static bool IsWindowsBrowser(string? label) => label is not null && label.EndsWith("· Windows", StringComparison.Ordinal);
    /// A browser (Web Push) on an Android phone. The native app's own row is never one, whatever its label says.
    internal static bool IsAndroidBrowser(string endpoint, string? label) =>
        !NativePush.IsNative(endpoint) && label is not null && label.EndsWith("· Android", StringComparison.Ordinal);

    /// A device can get this push only if its transport is on (Web Push for browsers, FCM for the native app).
    private bool Deliverable(string endpoint) => NativePush.IsNative(endpoint) ? NativeOn : transport.IsConfigured;

    private async Task ExpandCallAsync(PushJob job, DateTime now, CancellationToken ct)
    {
        if (job.RefId is not { } callId) return;
        var room = await LoadCallAsync(callId, ct);
        if (room is null) return;
        var invited = Invited(room, now);

        // Earlier rings/closes of this call, per user and ring start.
        var history = await db.PushDeliveries.AsNoTracking()
            .Where(d => d.Job.Kind == "call" && d.Job.RefId == callId)
            .Select(d => new { d.UserId, d.Kind, d.Key }).Distinct().ToListAsync(ct);
        var rung = history.Where(h => h.Kind == "call").Select(h => (h.UserId, h.Key)).ToHashSet();
        var closed = history.Where(h => h.Kind == "close").Select(h => (h.UserId, h.Key)).ToHashSet();

        var plan = new List<(Guid UserId, string Kind, string Key, DateTime Expires)>();
        foreach (var (u, since) in invited)
        {
            var key = since.Ticks.ToString();
            if (!rung.Contains((u, key))) plan.Add((u, "call", key, since + CallRingTimeout));
        }
        // Close a ring once the user is no longer invited. A re-invite replaces the toast via the same tag,
        // so no close is sent while any ring of theirs is live (it would dismiss the new one).
        foreach (var (u, key) in rung)
            if (!closed.Contains((u, key)) && !invited.ContainsKey(u))
                plan.Add((u, "close", key!, now + TimeSpan.FromHours(12)));
        plan.RemoveAll(p => p.Expires <= now);
        if (plan.Count == 0) return;

        var users = plan.Select(p => p.UserId).Distinct().ToList();
        var subs = await db.PushSubscriptions.AsNoTracking().Where(s => users.Contains(s.UserId))
            .Select(s => new { s.Id, s.UserId, s.Endpoint, s.Label }).ToListAsync(ct);
        var nativeUsers = subs.Where(s => NativePush.IsNative(s.Endpoint)).Select(s => s.UserId).ToHashSet();
        foreach (var p in plan)
        foreach (var s in subs.Where(s => s.UserId == p.UserId && Deliverable(s.Endpoint)
                     && !(nativeUsers.Contains(s.UserId) && IsAndroidBrowser(s.Endpoint, s.Label))))
            db.PushDeliveries.Add(NewDelivery(job, s.Id, s.UserId, s.Endpoint, p.Kind, p.Key, p.Expires, now));
    }

    private static PushDelivery NewDelivery(PushJob job, Guid subId, Guid userId, string endpoint, string kind, string? key,
        DateTime expires, DateTime now) => new()
    {
        Id = Guid.NewGuid(), JobId = job.Id, SubscriptionId = subId, UserId = userId, Kind = kind, Key = key,
        Provider = ProviderOf(endpoint), Status = Status.Queued, NextAttemptAt = now, ExpiresAt = expires,
        CreatedAt = now, UpdatedAt = now,
    };

    public async Task<int> SendDueAsync(DateTime now, CancellationToken ct)
    {
        var due = await db.PushDeliveries.AsNoTracking()
            .Where(d => (d.Status == Status.Queued || d.Status == Status.Retrying || d.Status == Status.Sending) && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt).Take(Batch)
            .Select(d => new { d.Id, d.Status, d.NextAttemptAt })
            .ToListAsync(ct);
        var claimedIds = new List<Guid>();
        foreach (var d in due)
        {
            var n = await db.PushDeliveries.Where(x => x.Id == d.Id && x.Status == d.Status && x.NextAttemptAt == d.NextAttemptAt)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, Status.Sending).SetProperty(x => x.NextAttemptAt, now + Lease), ct);
            if (n == 1) claimedIds.Add(d.Id);
        }
        if (claimedIds.Count == 0) return 0;

        var rows = await db.PushDeliveries.Include(d => d.Job).Where(d => claimedIds.Contains(d.Id)).ToListAsync(ct);
        var subIds = rows.Select(r => r.SubscriptionId).Distinct().ToList();
        var subs = await db.PushSubscriptions.Where(s => subIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var gone = new HashSet<Guid>();

        foreach (var group in rows.GroupBy(r => r.JobId))
        {
            var job = group.First().Job;
            var users = group.Select(r => r.UserId).Distinct().ToList();
            Audience? audience = null;
            CallRoomDto? call = null;
            if (job.Kind == "call") call = job.RefId is { } cid ? await LoadCallAsync(cid, ct) : null;
            else audience = await AudienceAsync(job, users, now, ct);

            foreach (var row in group)
            {
                row.Attempts++;
                row.UpdatedAt = now;
                if (now >= row.ExpiresAt) { Finish(row, Status.Expired, "expired"); continue; }
                if (!subs.TryGetValue(row.SubscriptionId, out var sub) || gone.Contains(sub.Id)) { Finish(row, Status.Suppressed, "unsubscribed"); continue; }
                // The browser now belongs to someone else (re-subscribed after an account switch).
                if (sub.UserId != row.UserId) { Finish(row, Status.Suppressed, "rebound"); continue; }

                var payload = job.Kind == "call" ? CallPayload(call, row, now) : audience?.PayloadFor(row.UserId);
                if (payload is null) { Finish(row, Status.Suppressed, job.Kind == "call" ? "stale-call" : "not-authorized"); continue; }
                payload = payload with
                {
                    ApiBase = sub.ApiBase, DeliveryId = row.Id.ToString("N"), JobId = job.Id.ToString("N"), Acct = AcctOf(sub.UserId),
                };
                var urgent = job.Kind == "call" || job.Kind == "test" || (audience?.Urgent ?? false);
                var ttl = row.ExpiresAt - now;
                var outcome = NativePush.IsNative(sub.Endpoint)
                    ? (native is { IsConfigured: true } n
                        ? await n.SendAsync(sub, payload, ttl, urgent, ct)
                        : new PushSendOutcome(PushSendResult.Rejected, "native-disabled"))
                    : await transport.SendAsync(sub, JsonSerializer.Serialize(payload, Json), ttl, urgent, TopicOf(payload.Tag), ct);
                Apply(row, outcome, now);
                if (row.Status == Status.Gone) gone.Add(sub.Id);
                // Final failures used to vanish into debug logs; a toast nobody got must be visible.
                else if (row.Status is Status.AuthError or Status.Rejected or Status.Failed)
                    log.LogWarning("Web push {Kind} delivery {DeliveryId} for user {UserId} failed permanently: {Status} ({Code}).",
                        job.Kind, row.Id, row.UserId, row.Status, row.Code ?? "-");
            }
        }

        if (gone.Count > 0)
        {
            db.PushSubscriptions.RemoveRange(subs.Values.Where(s => gone.Contains(s.Id)));
            log.LogInformation("Removed {Count} expired push subscription(s).", gone.Count);
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        foreach (var g in rows.GroupBy(r => (r.Status, r.Code)))
            log.LogDebug("Web push: {Count} delivery(ies) {Status} ({Code}).", g.Count(), g.Key.Status, g.Key.Code ?? "-");
        return rows.Count;
    }

    private static void Finish(PushDelivery row, string status, string code)
    {
        row.Status = status;
        row.Code = code;
    }

    /// Maps a transport outcome to the row: accepted is final, transient errors back off
    /// (honouring Retry-After) inside the original deadline, everything else is final.
    internal static void Apply(PushDelivery row, PushSendOutcome outcome, DateTime now)
    {
        row.Code = outcome.Code;
        switch (outcome.Result)
        {
            case PushSendResult.Sent:
                row.Status = Status.Accepted; row.AcceptedAt = now; row.Code = null; return;
            case PushSendResult.Gone:
                row.Status = Status.Gone; return;
            case PushSendResult.AuthError:
                row.Status = Status.AuthError; return;
            case PushSendResult.Rejected:
                row.Status = Status.Rejected; return;
        }
        var backoff = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, row.Attempts - 1)));
        var wait = outcome.RetryAfter is { } ra && ra > backoff ? ra : backoff;
        if (row.Attempts >= PushDelivery.MaxAttempts) { row.Status = Status.Failed; row.Code = "max-attempts"; }
        else if (now + wait >= row.ExpiresAt) { row.Status = Status.Expired; }
        else { row.Status = Status.Retrying; row.NextAttemptAt = now + wait; }
    }

    /// Drops outbox history past the retention window (deliveries cascade with their job).
    public Task<int> PurgeAsync(DateTime now, CancellationToken ct)
    {
        var cutoff = now - Retention;
        return db.PushJobs.Where(j => j.CreatedAt < cutoff
                && !j.Deliveries.Any(d => d.Status == Status.Queued || d.Status == Status.Retrying || d.Status == Status.Sending))
            .ExecuteDeleteAsync(ct);
    }

    // ── Audience: who may see the toast now, and what it says ─────────────────

    internal sealed record Audience(string Kind, IReadOnlyList<Guid> Users, Func<Guid, PushPayload?> PayloadFor, bool Urgent, DateTime ExpiresAt);

    private static readonly IReadOnlyList<Guid> NoUsers = [];

    /// Rebuilds the toast for a job from current data. Users outside the returned set get null.
    internal Task<Audience?> AudienceAsync(PushJob job, IReadOnlyCollection<Guid>? only, DateTime now, CancellationToken ct) => job.Kind switch
    {
        "chat" => ChatAudienceAsync(job, only, now, ct),
        "notif" => NotifAudienceAsync(job, only, now, ct),
        "test" => Task.FromResult<Audience?>(TestAudience(job, only)),
        _ => Task.FromResult<Audience?>(null),
    };

    private static Audience TestAudience(PushJob job, IReadOnlyCollection<Guid>? only)
    {
        var users = Restrict(ParseIds(job.UserIds), only);
        var tag = $"test:{job.Id:N}";
        return new Audience("test", users,
            uid => users.Contains(uid) ? new PushPayload("test", "iPop", "Notifications are on ✔", "/notifications/settings", tag) : null,
            true, job.CreatedAt + TimeSpan.FromMinutes(10));
    }

    /// A new chat message → a toast for every other member of the room, except where the
    /// room is muted/archived, the sender is blocked, chat is a muted type or quiet hours
    /// apply, or the member is @-mentioned (the mention notification toasts instead).
    private async Task<Audience?> ChatAudienceAsync(PushJob job, IReadOnlyCollection<Guid>? only, DateTime now, CancellationToken ct)
    {
        var msg = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Id == job.RefId)
            .Select(m => new { m.Id, m.RoomId, m.SenderId, SenderName = m.Sender.Name, m.Type, m.Content, m.IsDeleted, m.CreatedAt,
                RoomType = m.Room.Type, RoomName = m.Room.Name, Files = m.Attachments.Count })
            .FirstOrDefaultAsync(ct);
        if (msg is null || msg.IsDeleted || msg.Type == "system") return null;
        var emergency = msg.Type == "emergency";

        var membersQ = db.ChatRoomMembers.AsNoTracking()
            .Where(m => m.RoomId == msg.RoomId && m.LeftAt == null && m.UserId != msg.SenderId && m.User.IsActive);
        if (only is not null) membersQ = membersQ.Where(m => only.Contains(m.UserId));
        var members = await membersQ.Select(m => m.UserId).ToListAsync(ct);
        if (members.Count == 0) return new Audience("chat", NoUsers, _ => null, emergency, msg.CreatedAt);

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
        var recipients = (emergency
            ? members.Except(blockers)
            : members.Except(silencedRoom).Except(blockers).Except(mentioned).Except(silenced)).ToHashSet();

        var previewOff = emergency ? [] : await PreviewOffAsync(recipients, ct);
        var title = emergency ? $"🚨 EMERGENCY · {msg.RoomName ?? msg.SenderName}"
            : msg.RoomType == "group" ? $"{msg.SenderName} · {msg.RoomName ?? "Group"}" : msg.SenderName;
        var preview = msg.Type == "sticker" ? "🎨 Sticker" : ChatPreview(msg.Content, msg.Files);
        var url = $"/chat/{msg.RoomId}";
        var tag = emergency ? $"emergency:{msg.Id:N}" : $"chat:{msg.RoomId}";

        return new Audience("chat", recipients.ToList(),
            uid => recipients.Contains(uid)
                ? new PushPayload("chat", title, previewOff.Contains(uid) ? "New message" : preview, url, tag, RequireInteraction: emergency)
                : null,
            emergency, msg.CreatedAt + TimeSpan.FromHours(emergency ? 1 : 12));
    }

    /// Gateway notifications (mentions, emergencies, module events). The producer filtered the
    /// recipients; a retry re-checks quiet hours/muted types and, for chat links, room membership.
    private async Task<Audience?> NotifAudienceAsync(PushJob job, IReadOnlyCollection<Guid>? only, DateTime now, CancellationToken ct)
    {
        var req = await db.NotificationRequests.AsNoTracking().Where(r => r.Id == job.RefId)
            .Select(r => new { r.Id, r.Title, r.Body, r.TargetUrl, r.EventType, r.Priority }).FirstOrDefaultAsync(ct);
        if (req is null) return null;
        var eventType = job.EventType ?? req.EventType;
        var priority = job.Priority ?? req.Priority;
        var isChat = NotificationGatewayService.ClassifyEventType(eventType) == "chat";
        var emergency = eventType == "chat.emergency";

        var candidates = Restrict(ParseIds(job.UserIds), only);
        var users = await db.Users.AsNoTracking().Where(u => candidates.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        // Chat notifications share the room's tag so they replace that room's message toast.
        var roomId = isChat ? ChatRoomOf(req.TargetUrl) : null;
        if (roomId is { } rid)
            users = await db.ChatRoomMembers.AsNoTracking()
                .Where(m => m.RoomId == rid && m.LeftAt == null && users.Contains(m.UserId)).Select(m => m.UserId).ToListAsync(ct);
        if (!emergency)
        {
            var silenced = await SilencedAsync(db, users, eventType, priority, now, ct);
            users = users.Where(u => !silenced.Contains(u)).ToList();
        }
        var recipients = users.ToHashSet();
        var previewOff = isChat && !emergency ? await PreviewOffAsync(recipients, ct) : [];
        var tag = roomId is { } r ? $"chat:{r}" : $"notif:{req.Id:N}";
        var title = job.Title ?? req.Title;
        return new Audience(isChat ? "chat" : "notif", recipients.ToList(),
            uid => recipients.Contains(uid)
                ? new PushPayload(isChat ? "chat" : "notif", title, previewOff.Contains(uid) ? "New message" : req.Body,
                    req.TargetUrl ?? "/notifications", tag, RequireInteraction: emergency)
                : null,
            priority == "high", job.CreatedAt + TimeSpan.FromHours(emergency ? 1 : 24));
    }

    /// One line for the toast body: mention markup flattened, cards/attachments named.
    internal static string ChatPreview(string content, int attachments)
    {
        if (content.StartsWith("[poll]")) return "📊 Poll";
        if (content.StartsWith("[note]")) return "📝 Shared a note";
        var text = IPop.Application.Chat.Mentions.MentionPlainText.Project(content, 120).Trim();
        if (text.Length > 0) return text;
        return attachments switch { 0 => "New message", 1 => "📎 Attachment", _ => $"📎 {attachments} attachments" };
    }

    internal static Guid? ChatRoomOf(string? url) =>
        url is not null && url.StartsWith("/chat/") && url.Length >= 42 && Guid.TryParse(url.AsSpan(6, 36), out var id) ? id : null;

    // ── Calls ─────────────────────────────────────────────────────────────────

    private static TimeSpan CallRingTimeout => IPop.Infrastructure.Calls.CallService.RingTimeout;

    /// Invited users of a live call and their ring start; empty once the call is over.
    internal static Dictionary<Guid, DateTime> Invited(CallRoomDto room, DateTime now)
    {
        var live = room.Status == CallStatus.Ringing || (CallKind.IsLiveKit(room.Kind) && room.Status == CallStatus.Active);
        return live
            ? room.Participants.Where(p => p.Status == CallParticipantStatus.Invited && p.UserId != room.CreatorId && p.JoinedAt is not null)
                .ToDictionary(p => p.UserId, p => p.JoinedAt!.Value)
            : [];
    }

    /// The ring toast, only while this exact ring is still live; closes always go out.
    private PushPayload? CallPayload(CallRoomDto? room, PushDelivery row, DateTime now)
    {
        if (room is null) return null;
        var tag = $"call:{room.Id}";
        if (row.Kind == "close") return new PushPayload("close", "", "", null, tag);
        if (!Invited(room, now).TryGetValue(row.UserId, out var since) || since.Ticks.ToString() != row.Key) return null;
        var token = dataProtection.CreateProtector(CallActionPurpose).ToTimeLimitedDataProtector()
            .Protect($"{room.Id:N}|{row.UserId:N}", CallActionLifetime);
        if (InviteText(room, row.UserId) is { } invite)
            return new PushPayload("call", invite.Title, invite.Body, "/calls", tag, RequireInteraction: true, CallId: room.Id, ActionToken: token,
                Sender: room.Participants.FirstOrDefault(p => p.UserId == room.CreatorId)?.Name);
        var caller = room.Participants.FirstOrDefault(p => p.UserId == room.CreatorId)?.Name ?? "Someone";
        var isGroup = room.Kind == CallKind.Group;
        return new PushPayload("call", isGroup ? $"Group call · {room.Title ?? "Group"}" : $"Incoming call · {caller}",
            isGroup ? $"{caller} started a group call" : "iPop voice call", "/calls", tag,
            RequireInteraction: true, CallId: room.Id, ActionToken: token, Sender: isGroup ? room.Title ?? caller : caller);
    }

    private async Task<CallRoomDto?> LoadCallAsync(Guid callId, CancellationToken ct)
    {
        var r = await db.CallRooms.AsNoTracking().Include(c => c.Participants).ThenInclude(p => p.User).Include(c => c.ChatRoom)
            .AsSplitQuery().FirstOrDefaultAsync(c => c.Id == callId, ct);
        return r is null ? null : new CallRoomDto(r.Id, r.Kind, r.Status, null, r.CreatorId, r.StartedAt, r.EstablishedAt, r.EndedAt, r.Quality,
            r.Participants.Select(p => new CallParticipantDto(p.UserId, p.User?.Nik ?? "", p.User?.Name ?? "", p.Status,
                p.IsMuted, p.IsScreenSharing, p.LeftAt, p.JoinedAt, p.InvitedById)).ToList(),
            ChatRoomId: r.ChatRoomId, Title: r.ChatRoom?.Name);
    }

    /// Toast text for someone added to a running call ("Add person"); null for the original invitees.
    internal static (string Title, string Body)? InviteText(CallRoomDto room, Guid userId)
    {
        var me = room.Participants.FirstOrDefault(p => p.UserId == userId);
        if (me?.InvitedById is not { } by) return null;
        var inviter = room.Participants.FirstOrDefault(p => p.UserId == by)?.Name ?? "Someone";
        var others = room.Participants
            .Where(p => p.UserId != userId && p.UserId != by && p.Status == CallParticipantStatus.Accepted)
            .Select(p => p.Name).Take(3).ToList();
        var target = room.Kind == CallKind.Group ? $"the {room.Title ?? "group"} call"
            : others.Count > 0 ? "a call with " + string.Join(", ", others)
            : "a call";
        return ($"{inviter} is calling", $"{inviter} invited you to {target}");
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
        var silenced = prefs.Where(x => IsSilenced(x.Pref, x.TimeZoneId, eventType, priority, now))
            .Select(x => x.Pref.UserId).ToHashSet();
        silenced.UnionWith(await BusyAsync(db, userIds, now, ct));
        return silenced;
    }

    /// Users whose Busy mutes alerts right now (online, and a timed Busy not yet over).
    internal static async Task<List<Guid>> BusyAsync(IPopDbContext db, IReadOnlyCollection<Guid> userIds, DateTime now, CancellationToken ct)
    {
        var rows = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id) && u.PresenceManualStatus == "busy")
            .Select(u => new { u.Id, u.PresenceLastSeenAt, u.PresenceManualStatus, u.PresenceBusyUntilUtc })
            .ToListAsync(ct);
        return rows.Where(u => PresenceStatus.IsBusy(u.PresenceLastSeenAt, u.PresenceManualStatus, u.PresenceBusyUntilUtc, now))
            .Select(u => u.Id).ToList();
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Account binding for the service worker: first 16 hex of SHA-256("ipop-push:" + userId).
    /// Not reversible to the id without a candidate list; never logged.
    public static string AcctOf(Guid userId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("ipop-push:" + userId.ToString("D").ToLowerInvariant())))[..16];

    /// Push service family from the endpoint host — diagnostics never expose the endpoint itself.
    public static string ProviderOf(string endpoint)
    {
        if (NativePush.IsNative(endpoint)) return "fcm-app";
        var host = Uri.TryCreate(endpoint, UriKind.Absolute, out var u) ? u.Host : "";
        return host.EndsWith("googleapis.com") ? "fcm"
            : host.EndsWith("mozilla.com") || host.EndsWith("mozaws.net") ? "mozilla"
            : host.EndsWith("notify.windows.com") ? "wns"
            : host.EndsWith("push.apple.com") ? "apple"
            : "other";
    }

    internal static string JoinIds(IEnumerable<Guid> ids) => string.Join(',', ids.Distinct().Select(i => i.ToString("N")));

    private static List<Guid> ParseIds(string? ids) =>
        (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToList();

    private static List<Guid> Restrict(List<Guid> ids, IReadOnlyCollection<Guid>? only) =>
        only is null ? ids : ids.Where(only.Contains).ToList();

    /// RFC 8030 topic (≤32 URL-safe chars): a newer message replaces a still-queued one with the same tag.
    private static string? TopicOf(string tag)
    {
        var clean = new string(tag.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return clean.Length == 0 ? null : clean.Length <= 32 ? clean : clean[..32];
    }
}
