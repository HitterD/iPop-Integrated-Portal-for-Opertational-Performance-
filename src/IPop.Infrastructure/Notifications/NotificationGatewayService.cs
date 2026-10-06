using IPop.Application.Auth;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Core.Presence;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notifications;

/// <summary>
/// Intelligent Notification Gateway implementation.
/// v1 scope: client-credential auth stub, targeting expansion into per-recipient deliveries,
/// lifecycle state machine, optional header rate limiter. Delivery itself is a stub — the
/// router decides SignalR vs push from presence, marks "delivered", and logs. A real async
/// worker (outbox pattern) replaces the in-line delivery step in v2; the outbox columns and
/// transitions are already in place.
/// </summary>
public sealed class NotificationGatewayService(
    IPopDbContext db,
    IPasswordHasher passwordHasher,
    ILogger<NotificationGatewayService> logger,
    INotificationRealtimeBroadcaster broadcaster,
    Push.PushQueue? push = null,
    IPop.Application.Realtime.IRealtime? realtime = null) : INotificationGatewayService
{
    private const int MaxAttempts = 3;

    public async Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(
        string clientId, string secret, string? remoteIp, CancellationToken ct)
    {
        var client = await db.NotificationGatewayClients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client is null) return null;
        if (!client.IsActive) return null;
        if (!passwordHasher.Verify(secret, client.SecretHash)) return null;
        if (!IsIpAllowed(client.AllowedIps, remoteIp)) return null;

        return new NotificationGatewayClientInfo(
            client.Id, client.ClientId, client.IsActive,
            client.RateLimitPerSecond, client.MaxRecipients,
            client.AllowedEventTypes is null ? [] : client.AllowedEventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            client.AllowedEventTypes is null ? -1 : client.AllowedEventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    public async Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct)
    {
        // ── Validation (before anything is written) ─────────────────────────────
        if (req.Priority is not ("high" or "normal" or "low"))
            return Fail(NotificationErrors.InvalidPriority, "Priority must be \"high\", \"normal\" or \"low\".");
        if (req.Target is null)
            return Fail(NotificationErrors.NoTargetSpecified, "Target is required."); // system-to-system: omit field → 422, not a 500

        var client = await db.NotificationGatewayClients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct);
        if (client is null || !client.IsActive)
            return Fail(NotificationErrors.ClientInactive, "Client does not exist or is inactive.");

        if (!EventTypeAllowed(client.AllowedEventTypes, req.EventType))
            return Fail(NotificationErrors.EventTypeNotAllowed, $"Event type \"{req.EventType}\" is not allowed for this client.");

        // ── Resolve targets → recipient user ids (expansion, no writes yet) ─────
        var recipients = await ResolveTargetsAsync(req.Target, ct);
        if (recipients is null)
            return Fail(NotificationErrors.TargetNotFound, "Target could not be resolved to any users.");
        if (recipients.Count == 0)
            return Fail(NotificationErrors.NoTargetSpecified, "Target did not match any active users.");
        if (recipients.Count > client.MaxRecipients)
            return Fail(NotificationErrors.TooManyRecipients, $"Target resolves to {recipients.Count} recipients; client max is {client.MaxRecipients}.");

        // ── Idempotency check (re-checked at insert to survive races) ───────────
        var existing = await db.NotificationRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ClientId == clientId && r.IdempotencyKey == req.IdempotencyKey, ct);
        if (existing is not null)
        {
            var count = await db.NotificationDeliveries.AsNoTracking().CountAsync(d => d.RequestId == existing.Id, ct);
            return new NotificationEnqueueResult(true, null, null, existing.Id, existing.Status, count, existing.AcceptedAt);
        }

        // ── Author the request + deliveries (Accepted -> Queued -> Processing) ──
        var now = DateTime.UtcNow;
        var request = new NotificationRequest
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            IdempotencyKey = req.IdempotencyKey,
            Priority = req.Priority,
            EventType = req.EventType,
            Title = req.Title,
            Body = req.Body,
            WebhookUrl = req.WebhookUrl,
            TargetUrl = req.TargetUrl,
            TargetEmployeeNik = req.Target.EmployeeNik,
            TargetDepartmentId = req.Target.DepartmentId,
            TargetRole = req.Target.Role,
            TargetAll = req.Target.All,
            Status = NotificationStatus.Accepted,
            AcceptedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.NotificationRequests.Add(request);

        foreach (var userId in recipients)
        {
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                Id = Guid.NewGuid(),
                RequestId = request.Id,
                UserId = userId,
                Status = NotificationStatus.Queued,
                AttemptCount = 0,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Race: another request with the same idempotency key won. Return the winner.
            db.ChangeTracker.Clear();
            var winner = await db.NotificationRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ClientId == clientId && r.IdempotencyKey == req.IdempotencyKey, ct);
            if (winner is not null)
            {
                var count = await db.NotificationDeliveries.AsNoTracking().CountAsync(d => d.RequestId == winner.Id, ct);
                return new NotificationEnqueueResult(true, null, null, winner.Id, winner.Status, count, winner.AcceptedAt);
            }
            throw;
        }

        // ── Process in-line (v1 stub; v2 moves this to a worker) ────────────────
        request.Status = NotificationStatus.Processing;
        request.QueuedAt = now;
        request.UpdatedAt = now;
        await ProcessDeliveriesAsync(request.Id, ct);

        return new NotificationEnqueueResult(
            true, null, null, request.Id, request.Status,
            await db.NotificationDeliveries.AsNoTracking().CountAsync(d => d.RequestId == request.Id, ct),
            request.AcceptedAt);
    }

    public async Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct)
    {
        var delivery = await db.NotificationDeliveries.FirstOrDefaultAsync(d => d.RequestId == requestId && d.UserId == userId, ct);
        if (delivery is null) return null;
        if (delivery.Status is NotificationStatus.Read or NotificationStatus.Acknowledged)
            return ToState(delivery);

        delivery.Status = NotificationStatus.Read;
        delivery.ReadAt = DateTime.UtcNow;
        delivery.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToState(delivery);
    }

    public async Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var deliveries = await db.NotificationDeliveries
            .Where(d => d.UserId == userId &&
                        d.Status != NotificationStatus.Read &&
                        d.Status != NotificationStatus.Acknowledged)
            .ToListAsync(ct);
        if (deliveries.Count == 0) return 0;

        foreach (var d in deliveries)
        {
            d.Status = NotificationStatus.Read;
            d.ReadAt = now;
            d.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return deliveries.Count;
    }

    public async Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct)
    {
        var delivery = await db.NotificationDeliveries.FirstOrDefaultAsync(d => d.RequestId == requestId && d.UserId == userId, ct);
        if (delivery is null) return null;
        if (delivery.Status == NotificationStatus.Acknowledged)
            return ToState(delivery);

        var now = DateTime.UtcNow;
        delivery.Status = NotificationStatus.Acknowledged;
        delivery.ReadAt ??= now; // acknowledged implies read
        delivery.AcknowledgedAt = now;
        delivery.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ToState(delivery);
    }

    public async Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct)
    {
        var delivery = await db.NotificationDeliveries.FirstOrDefaultAsync(d => d.RequestId == requestId && d.UserId == userId, ct);
        if (delivery is null) return null;
        if (delivery.Status is NotificationStatus.Read or NotificationStatus.Acknowledged)
            return ToState(delivery); // terminal states are not retried
        if (delivery.AttemptCount >= MaxAttempts)
        {
            delivery.Status = NotificationStatus.Failed;
            delivery.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ToState(delivery);
        }

        delivery.Status = NotificationStatus.Queued;
        delivery.AttemptCount++;
        delivery.NextAttemptAt = DateTime.UtcNow.AddSeconds(BackoffSeconds(delivery.AttemptCount));
        delivery.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToState(delivery);
    }

    public async Task<NotificationEnqueueResult> PublishInternalAsync(
        string clientSlug,
        string eventType,
        string title,
        string message,
        NotificationTarget target,
        string priority = "normal",
        string? targetUrl = null,
        CancellationToken ct = default)
    {
        var mod = string.IsNullOrWhiteSpace(clientSlug) ? "system" : clientSlug.Trim().ToLowerInvariant();
        var client = await db.NotificationGatewayClients.FirstOrDefaultAsync(c => c.ClientId == mod, ct);
        if (client is null)
        {
            client = new NotificationGatewayClient
            {
                Id = Guid.NewGuid(),
                ClientId = mod,
                SecretHash = "internal-no-secret",
                AllowedIps = null,
                AllowedEventTypes = "*",
                RateLimitPerSecond = 0,
                MaxRecipients = 10000,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            db.NotificationGatewayClients.Add(client);
            await db.SaveChangesAsync(ct);
        }

        var req = new NotificationEnqueueRequest(
            IdempotencyKey: $"{mod}-{Guid.NewGuid():N}",
            Priority: priority,
            EventType: eventType,
            Title: title,
            Body: message,
            Target: target,
            WebhookUrl: null,
            TargetUrl: targetUrl);

        return await EnqueueAsync(client.Id, req, ct);
    }

    // ── Internals ────────────────────────────────────────────────────────────────

    private async Task ProcessDeliveriesAsync(Guid requestId, CancellationToken ct)
    {
        var deliveries = await db.NotificationDeliveries.Where(d => d.RequestId == requestId).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var request = await db.NotificationRequests.AsNoTracking().FirstAsync(r => r.Id == requestId, ct);

        // Channel stub: online (presence) → signalr; off/away → push. Routing per recipient.
        var userPresence = await db.Users.AsNoTracking()
            .Where(u => deliveries.Select(d => d.UserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.PresenceLastSeenAt, ct);

        var silenced = new HashSet<Guid>(); // quiet hours / muted type: no Windows toast either
        foreach (var d in deliveries)
        {
            var lastSeen = userPresence.GetValueOrDefault(d.UserId);
            var status = PresenceStatus.ComputeStatus(lastSeen, null, now);
            d.Channel = status == "on" || status == "away" ? NotificationChannel.SignalR : NotificationChannel.Push;
            d.Status = NotificationStatus.Delivered;
            d.AttemptCount = 1;
            d.LastAttemptAt = now;
            d.NextAttemptAt = null;
            d.DeliveredAt = now;
            d.UpdatedAt = now;

            // Fase 4: cek preferensi jam senyap & jenis yang dibisukan
            var pref = await db.NotificationPreferences.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == d.UserId, ct);
            if (pref is not null)
            {
                if (pref.QuietHoursEnabled)
                {
                    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == d.UserId, ct);
                    var timeZoneId = user?.TimeZoneId ?? "SE Asia Standard Time";
                    var userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

                    if (IsInQuietHours(pref, now, userTimeZone))
                    {
                        if (request.Priority != "high" || !pref.AllowImportant)
                        {
                            d.Channel = NotificationChannel.Push; // deferred — not shown as toast
                            silenced.Add(d.UserId);
                            logger.LogDebug("Notification deferred (quiet hours): user {UserId}, request {RequestId}", d.UserId, d.RequestId);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(pref.MutedTypes))
                {
                    var mod = ClassifyEventType(request.EventType);
                    var muted = pref.MutedTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (muted.Contains(mod, StringComparer.OrdinalIgnoreCase) || muted.Contains(request.EventType, StringComparer.OrdinalIgnoreCase))
                    {
                        d.Channel = NotificationChannel.Push; // suppressed from realtime SignalR toast
                        silenced.Add(d.UserId);
                        logger.LogDebug("Notification suppressed (muted type {Type}): user {UserId}, request {RequestId}", mod, d.UserId, d.RequestId);
                    }
                }
            }

            // v2: enqueue a real send job here; v1 stub marks delivered + logs.
            logger.LogInformation("Notification delivery stub: {Status} -> user {UserId} via {Channel} (request {RequestId})",
                d.Status, d.UserId, d.Channel, d.RequestId);
        }

        await db.SaveChangesAsync(ct);

        // Request aggregate status mirrors delivery outcome. Success only when every
        // delivery made it; any dead-lettered delivery flips the request to Failed.
        var requestToUpdate = await db.NotificationRequests.FirstAsync(r => r.Id == requestId, ct);
        requestToUpdate.DeliveredAt = now;
        if (deliveries.Count > 0 && deliveries.All(d => d.Status == NotificationStatus.Delivered))
        {
            requestToUpdate.Status = NotificationStatus.Delivered;
        }
        else
        {
            requestToUpdate.Status = NotificationStatus.Failed;
            requestToUpdate.FailedAt = now;
        }
        requestToUpdate.UpdatedAt = now;
        // Windows toast (Web Push) intent for every recipient not silenced by their preferences —
        // online or not; saved with the request's final state. The worker re-checks preferences on send.
        var toast = deliveries.Select(d => d.UserId).Where(u => !silenced.Contains(u)).Distinct().ToList();
        if (push is not null && toast.Count > 0)
            Push.PushQueue.Add(db, "notif", requestId, toast, request.EventType, request.Priority);
        await db.SaveChangesAsync(ct);
        push?.Notify();

        // Real-time push: for every delivery that just landed, notify the recipient's
        // SignalR connection(s). Fan-out happens per-user — each user only gets their own.
        var states = deliveries
            .Where(d => d.Channel == NotificationChannel.SignalR)
            .Select(d => ToState(d, request))
            .ToList();
        if (states.Count > 0)
            await broadcaster.BroadcastAsync(states, ct);

        // Quiet hours / muted type: no banner, sound or toast — but an open bell must still count
        // it. A quiet "notificationsChanged" makes the client re-read its bell without any banner.
        if (realtime is not null && silenced.Count > 0)
        {
            try
            {
                var niks = await db.Users.AsNoTracking().Where(u => silenced.Contains(u.Id)).Select(u => u.Nik).ToListAsync(ct);
                await realtime.ToUsersAsync(niks, IPop.Application.Realtime.RealtimeEvents.NotificationsChanged, ct: ct);
            }
            catch (Exception ex) { logger.LogDebug(ex, "Quiet bell refresh failed (non-fatal)"); }
        }
    }

    /// <summary>
    /// Cek apakah waktu saat ini masuk dalam rentang jam senyap pengguna.
    /// Memperhitungkan hari aktif dan rentang yang melintasi tengah malam.
    /// Internal agar dapat diuji langsung dari IPop.Tests.
    /// </summary>
    internal static string ClassifyEventType(string eventType)
    {
        var e = eventType.ToLowerInvariant();
        if (e.StartsWith("chat.") || e == "chat") return "chat";
        if (e.StartsWith("mail.") || e == "mail") return "mail";
        if (e.StartsWith("news.") || e.StartsWith("memo.") || e == "news") return "news";
        if (e.StartsWith("project.") || e.StartsWith("task.") || e == "project") return "project";
        return "system";
    }

    internal static bool IsInQuietHours(NotificationPreference pref, DateTime now, TimeZoneInfo userTimeZone)
    {
        if (!pref.QuietHoursEnabled)
            return false;

        var startStr = string.IsNullOrWhiteSpace(pref.QuietStart) ? "22:00" : pref.QuietStart;
        var endStr = string.IsNullOrWhiteSpace(pref.QuietEnd) ? "07:00" : pref.QuietEnd;

        if (!TimeOnly.TryParse(startStr, out var start) || !TimeOnly.TryParse(endStr, out var end))
            return false;

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, userTimeZone);
        var current = TimeOnly.FromDateTime(localNow);

        // Cek hari aktif: jika QuietDays diisi, hanya hari tersebut yang berlaku
        if (!string.IsNullOrWhiteSpace(pref.QuietDays))
        {
            var activeDays = pref.QuietDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var dayMap = new Dictionary<string, string>
            {
                ["monday"] = "mon", ["tuesday"] = "tue", ["wednesday"] = "wed",
                ["thursday"] = "thu", ["friday"] = "fri", ["saturday"] = "sat", ["sunday"] = "sun"
            };
            var todayShort = dayMap.GetValueOrDefault(localNow.DayOfWeek.ToString().ToLowerInvariant(), "");
            if (!activeDays.Contains(todayShort))
                return false; // Hari ini tidak termasuk jam senyap
        }

        if (start <= end)
        {
            // Rentang normal: 08:00–17:00
            return current >= start && current < end;
        }
        else
        {
            // Rentang lintas tengah malam: 22:00–07:00
            return current >= start || current < end;
        }
    }

    /// <summary>
    /// Expands a targeting criteria into user ids. Employee → NIK match; Department →
    /// users assigned to the department (or to a division inside it); Role → users carrying
    /// the role; All → every active user. Returns null when the criteria can't be resolved
    /// (e.g. a department id that does not exist), empty when no users match.
    /// </summary>
    private async Task<List<Guid>?> ResolveTargetsAsync(NotificationTarget target, CancellationToken ct)
    {
        if (target.All)
        {
            return await db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => u.Id).ToListAsync(ct);
        }

        if (target.UserIds is { Count: > 0 })
        {
            return await db.Users.AsNoTracking()
                .Where(u => u.IsActive && target.UserIds.Contains(u.Id))
                .Select(u => u.Id).ToListAsync(ct);
        }

        if (target.UserId is not null)
        {
            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == target.UserId.Value && u.IsActive, ct);
            return user is null ? null : [user.Id];
        }

        if (!string.IsNullOrWhiteSpace(target.EmployeeNik))
        {
            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Nik == target.EmployeeNik && u.IsActive, ct);
            return user is null ? null : [user.Id];
        }

        if (target.DepartmentId is not null)
        {
            var exists = await db.Departments.AsNoTracking().AnyAsync(d => d.Id == target.DepartmentId, ct);
            if (!exists) return null;
            return await db.Users.AsNoTracking().Where(u => u.IsActive && u.DepartmentId == target.DepartmentId).Select(u => u.Id).ToListAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(target.Role))
        {
            var roleExists = await db.Roles.AsNoTracking().AnyAsync(r => r.Name == target.Role, ct);
            if (!roleExists) return null;
            return await db.Users.AsNoTracking()
                .Where(u => u.IsActive && u.UserRoles.Any(ur => ur.Role.Name == target.Role))
                .Select(u => u.Id).ToListAsync(ct);
        }

        return [];
    }

    private static bool IsIpAllowed(string? allowedIps, string? remoteIp)
    {
        // v1 stub: empty allowlist = everything passes (trusted internal network);
        // a list of entries is checked for exact membership. CIDR ranges are v2.
        if (string.IsNullOrWhiteSpace(allowedIps)) return true;
        if (string.IsNullOrWhiteSpace(remoteIp)) return false;
        return allowedIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(remoteIp, StringComparer.OrdinalIgnoreCase);
    }

    /// Event-type gate: empty/null = all allowed; "*" is an explicit wildcard; otherwise
    /// exact (case-insensitive) membership in the CSV list.
    private static bool EventTypeAllowed(string? allowedEventTypes, string eventType)
    {
        if (string.IsNullOrWhiteSpace(allowedEventTypes)) return true;
        var list = allowedEventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Contains("*", StringComparer.OrdinalIgnoreCase)
            || list.Contains(eventType, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static int BackoffSeconds(int attempt) => (int)Math.Pow(2, attempt - 1); // 1,2,4

    private static NotificationDeliveryState ToState(NotificationDelivery d, NotificationRequest? req = null) =>
        new(d.RequestId, d.UserId, d.Status, d.Channel, d.AttemptCount, d.DeliveredAt, d.ReadAt, d.AcknowledgedAt,
            req?.Title ?? d.Request?.Title,
            req?.Body ?? d.Request?.Body,
            req?.EventType ?? d.Request?.EventType,
            req?.Priority ?? d.Request?.Priority,
            req?.TargetUrl ?? d.Request?.TargetUrl);

    private static NotificationEnqueueResult Fail(string code, string error) =>
        new(false, error, code, Guid.Empty, "", 0, default);
}
