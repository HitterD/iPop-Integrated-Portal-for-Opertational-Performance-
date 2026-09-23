using IPop.Application.Auth;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// <summary>
/// Intelligent Notification Gateway — the single API internal apps (HRIS/ERP/CRM) use to
/// send notifications centrally. Client auth is header-based: X-Client-Id + X-Client-Secret
/// (v1 simple check, not full OAuth). Rate limiting is a stub (per-second counter in memory).
/// </summary>
public static class NotificationGatewayEndpoints
{
    /// <summary>
    /// In-memory sliding-window rate limiter (v1 stub). Keyed by client id; a real limiter
    /// (Redis-based, distributed) replaces this in v2. Reset window every fixed second bucket.
    /// </summary>
    private sealed class RateBucket
    {
        public long WindowStartSec { get; set; }
        public int Count { get; set; }
    }

    private static readonly Dictionary<Guid, RateBucket> RateBuckets = new();
    private static readonly SemaphoreSlim RateLock = new(1, 1);

    public static IEndpointRouteBuilder MapNotificationGateway(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/notifications/v1");

        // ────────────────────────────────────────────────────────────────────────
        // Admin: register a new client (system-to-system credential provisioning).
        // ────────────────────────────────────────────────────────────────────────
        v1.MapPost("/clients", async (RegisterClientRequest req,
            IPopDbContext db, IPasswordHasher hasher, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ClientId) || string.IsNullOrWhiteSpace(req.Secret))
                return Results.BadRequest(new { code = "invalid_client", message = "ClientId and Secret are required." });
            if (await db.NotificationGatewayClients.AnyAsync(c => c.ClientId == req.ClientId, ct))
                return Results.Conflict(new { code = "client_exists", message = "ClientId already registered." });
            if (req.RateLimitPerSecond < 0 || req.MaxRecipients <= 0)
                return Results.BadRequest(new { code = "invalid_limits", message = "RateLimitPerSecond must be >= 0 and MaxRecipients > 0." });

            var client = new NotificationGatewayClient
            {
                Id = Guid.NewGuid(),
                ClientId = req.ClientId.Trim(),
                SecretHash = hasher.Hash(req.Secret),       // never store the plaintext secret
                AllowedIps = string.IsNullOrWhiteSpace(req.AllowedIps) ? null : string.Join(',', req.AllowedIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                AllowedEventTypes = string.IsNullOrWhiteSpace(req.AllowedEventTypes) ? null : string.Join(',', req.AllowedEventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                RateLimitPerSecond = req.RateLimitPerSecond,
                MaxRecipients = req.MaxRecipients,
                CreatedAt = DateTime.UtcNow,
            };
            db.NotificationGatewayClients.Add(client);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/notifications/v1/clients/{client.Id}",
                new { client.Id, client.ClientId, client.AllowedIps, client.AllowedEventTypes, client.RateLimitPerSecond, client.MaxRecipients });
        }).RequireAuthorization("AdminOnly");

        // ────────────────────────────────────────────────────────────────────────
        // Client send: auth by X-Client-Id / X-Client-Secret headers (v1 simple).
        // ────────────────────────────────────────────────────────────────────────
        v1.MapPost("/notifications", async (NotificationEnqueueRequest req,
            HttpContext http, INotificationGatewayService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var clientId = http.Request.Headers["X-Client-Id"].FirstOrDefault();
            var secret = http.Request.Headers["X-Client-Secret"].FirstOrDefault();
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret))
                return Results.Unauthorized();

            var client = await svc.AuthenticateClientAsync(clientId, secret, http.Connection.RemoteIpAddress?.ToString(), ct);
            if (client is null)
                return Results.Unauthorized();

            if (!await TryTakeRateTicketAsync(client.Id, client.RateLimitPerSecond, ct))
                return Results.Json(new { code = NotificationErrors.RateLimited, message = "Rate limit exceeded." }, statusCode: 429);

            var result = await svc.EnqueueAsync(client.Id, req, ct);
            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    NotificationErrors.InvalidPriority or NotificationErrors.NoTargetSpecified or NotificationErrors.TooManyRecipients or NotificationErrors.EventTypeNotAllowed => 422,
                    NotificationErrors.TargetNotFound => 404,
                    NotificationErrors.IdempotencyConflict => 409,
                    _ => 400,
                };
                return Results.Json(new { code = result.ErrorCode, message = result.Error }, statusCode: statusCode);
            }
            return Results.Accepted($"/api/notifications/v1/notifications/{result.RequestId}", new
            {
                result.RequestId,
                result.Status,
                result.DeliveryCount,
                result.AcceptedAt,
            });
        });

        // ────────────────────────────────────────────────────────────────────────
        // Recipient-side status: mark read / acknowledge (user JWT). These phases of the
        // lifecycle are client-driven on the recipient side (app in-app notifications).
        // ────────────────────────────────────────────────────────────────────────
        v1.MapPost("/notifications/{requestId:guid}/read", async (Guid requestId,
            HttpContext http, INotificationGatewayService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var state = await svc.MarkReadAsync(requestId, userId.Value, ct);
            return state is null
                ? Results.Json(new { code = NotificationErrors.DeliveryNotFound, message = "No delivery found for this user/request." }, statusCode: 404)
                : Results.Ok(state);
        }).RequireAuthorization();

        // GET /api/notifications/v1/unread-count — jumlah belum dibaca untuk badge.
        // Ringan (satu COUNT) agar shell bisa mem-poll tanpa memuat seluruh inbox.
        v1.MapGet("/unread-count", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var unread = await db.NotificationDeliveries.AsNoTracking()
                .CountAsync(d => d.UserId == userId.Value
                    && d.Status != NotificationStatus.Read
                    && d.Status != NotificationStatus.Acknowledged, ct);
            return Results.Ok(new { unread });
        }).RequireAuthorization();

        // ── Preferensi notifikasi (Fase 4): jam senyap + jenis yang dibisukan ──

        // GET /api/notifications/v1/preferences — baca preferensi pengguna (default bila belum ada).
        // Sertakan zona waktu EFEKTIF pengguna (profil ?? default server) agar frontend
        // mengevaluasi jam senyap dalam zona waktu yang sama persis dengan backend.
        v1.MapGet("/preferences", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var p = await db.NotificationPreferences.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserId == userId.Value, ct);
            var tzProfile = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId.Value)
                .Select(u => u.TimeZoneId)
                .FirstOrDefaultAsync(ct);
            // Default sama dengan fallback di NotificationGatewayService (WIB).
            var tzEffective = string.IsNullOrWhiteSpace(tzProfile) ? "SE Asia Standard Time" : tzProfile;
            return Results.Ok(p is null
                ? new NotificationPreferenceDto(false, null, null, "", "", true, tzEffective)
                : new NotificationPreferenceDto(p.QuietHoursEnabled, p.QuietStart, p.QuietEnd, p.MutedTypes, p.QuietDays, p.AllowImportant, tzEffective));
        }).RequireAuthorization();

        // PUT /api/notifications/v1/preferences — simpan jam senyap + jenis yang dibisukan.
        v1.MapPut("/preferences", async (NotificationPreferenceDto req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();

            // Validasi format jam (HH:mm) bila jam senyap aktif.
            if (req.QuietHoursEnabled)
            {
                if (string.IsNullOrWhiteSpace(req.QuietStart) || string.IsNullOrWhiteSpace(req.QuietEnd)
                    || !TimeOnly.TryParse(req.QuietStart, out _) || !TimeOnly.TryParse(req.QuietEnd, out _))
                    return Results.Json(new { code = "invalid_quiet_hours", message = "Quiet hours format must be HH:mm." }, statusCode: 422);
            }

            var p = await db.NotificationPreferences.FirstOrDefaultAsync(x => x.UserId == userId.Value, ct);
            if (p is null)
            {
                p = new NotificationPreference { Id = Guid.NewGuid(), UserId = userId.Value };
                db.NotificationPreferences.Add(p);
            }
            p.QuietHoursEnabled = req.QuietHoursEnabled;
            p.QuietStart = req.QuietHoursEnabled ? req.QuietStart : null;
            p.QuietEnd = req.QuietHoursEnabled ? req.QuietEnd : null;
            p.QuietDays = req.QuietDays ?? "";
            p.AllowImportant = req.AllowImportant;
            p.MutedTypes = req.MutedTypes ?? "";
            p.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new NotificationPreferenceDto(p.QuietHoursEnabled, p.QuietStart, p.QuietEnd, p.MutedTypes, p.QuietDays, p.AllowImportant));
        }).RequireAuthorization();

        // PUT /api/notifications/v1/preferences/timezone — simpan zona waktu pengguna
        // (disimpan di User.TimeZoneId; dipakai backend untuk evaluasi jam senyap).
        v1.MapPut("/preferences/timezone", async (TimezoneRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();

            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(req.TimeZoneId); }
            catch { return Results.Json(new { code = "invalid_timezone", message = "TimeZoneId is not recognized." }, statusCode: 422); }

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
            if (user is null) return Results.Unauthorized();
            user.TimeZoneId = tz.Id;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { timeZoneId = tz.Id });
        }).RequireAuthorization();

        // ────────────────────────────────────────────────────────────────────────
        // Recipient inbox "mark all read" — one call, per-user, JWT-gated.
        // ────────────────────────────────────────────────────────────────────────
        v1.MapPost("/inbox/read-all", async (HttpContext http,
            INotificationGatewayService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var updated = await svc.MarkAllReadAsync(userId.Value, ct);
            return Results.Ok(new { updated });
        }).RequireAuthorization();

        v1.MapPost("/notifications/{requestId:guid}/acknowledge", async (Guid requestId,
            HttpContext http, INotificationGatewayService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var state = await svc.MarkAcknowledgedAsync(requestId, userId.Value, ct);
            return state is null
                ? Results.Json(new { code = NotificationErrors.DeliveryNotFound, message = "No delivery found for this user/request." }, statusCode: 404)
                : Results.Ok(state);
        }).RequireAuthorization();

        // ────────────────────────────────────────────────────────────────────────
        // Client retry: schedules or dead-letters a delivery (v1 stub; worker in v2).
        // ────────────────────────────────────────────────────────────────────────
        v1.MapPost("/notifications/{requestId:guid}/retry", async (Guid requestId,
            RetryRequest req, HttpContext http, INotificationGatewayService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var client = await AuthenticateRetryClientAsync(http, svc, ct);
            if (client is null)
                return Results.Unauthorized();
            if (!await db.NotificationRequests.AsNoTracking().AnyAsync(r => r.Id == requestId && r.ClientId == client.Id, ct))
                return Results.Json(new { code = "request_not_found", message = "Request not found for this client." }, statusCode: 404);

            var nik = req.UserNik;
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null)
                return Results.Json(new { code = NotificationErrors.TargetNotFound, message = "User not found." }, statusCode: 404);

            var state = await svc.RetryDeliveryAsync(requestId, user.Id, ct);
            return state is null
                ? Results.Json(new { code = NotificationErrors.DeliveryNotFound, message = "No delivery found for this user/request." }, statusCode: 404)
                : Results.Ok(state);
        });

        // ────────────────────────────────────────────────────────────────────────
        // Admin: client list — registered gateway clients (never the secret hash).
        // ────────────────────────────────────────────────────────────────────────
        v1.MapGet("/clients", async (IPopDbContext db, CancellationToken ct) =>
        {
            var items = await ListClientsCore(db, ct);
            return Results.Ok(new { items, total = items.Count });
        }).RequireAuthorization("AdminOnly");

        // ────────────────────────────────────────────────────────────────────────
        // Admin: request list — with status/priority filters + counts for the
        // dashboard (queued/failed/read) and the viewer's request picker.
        // ────────────────────────────────────────────────────────────────────────
        v1.MapGet("/requests", async (string? status, string? priority, string? clientId,
            int? page, int? pageSize, IPopDbContext db, CancellationToken ct) =>
        {
            var result = await ListRequestsCore(db, status, priority, clientId, page ?? 1, pageSize ?? 20, ct);
            return Results.Ok(result);
        }).RequireAuthorization("AdminOnly");

        // ────────────────────────────────────────────────────────────────────────
        // Admin: per-client requests (optional enumerate — client picker detail).
        // ────────────────────────────────────────────────────────────────────────
        v1.MapGet("/clients/{clientId:guid}/requests", async (Guid clientId, int? page, int? pageSize,
            IPopDbContext db, CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            if (!await db.NotificationGatewayClients.AsNoTracking().AnyAsync(c => c.Id == clientId, ct))
                return Results.Json(new { code = "client_not_found", message = "Client not found." }, statusCode: 404);

            var result = await ListClientRequestsCore(db, clientId, p, size, ct);
            return Results.Ok(result);
        }).RequireAuthorization("AdminOnly");

        // ────────────────────────────────────────────────────────────────────────
        // Admin: DLQ viewer — list deliveries stuck in "failed" (dead-letter queue).
        // ────────────────────────────────────────────────────────────────────────
        v1.MapGet("/dead-letter", async (int? page, int? pageSize,
            IPopDbContext db, CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var q = db.NotificationDeliveries.AsNoTracking()
                .Where(d => d.Status == NotificationStatus.Failed);
            var total = await q.CountAsync(ct);
            var items = await q
                .OrderByDescending(d => d.UpdatedAt)
                .Skip((p - 1) * size).Take(size)
                .Select(d => new
                {
                    d.Id,
                    d.RequestId,
                    d.UserId,
                    d.Status,
                    d.Channel,
                    d.AttemptCount,
                    d.LastError,
                    d.NextAttemptAt,
                    d.UpdatedAt,
                    ClientId = d.Request.Client.ClientId,
                })
                .ToListAsync(ct);
            return Results.Ok(new { items, total, page = p, pageSize = size });
        }).RequireAuthorization("AdminOnly");

        // ────────────────────────────────────────────────────────────────────────
        // Admin: request timeline — full lifecycle statuses for instant/status auditing.
        // ────────────────────────────────────────────────────────────────────────
        v1.MapGet("/requests/{requestId:guid}/timeline", async (Guid requestId,
            HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var request = await db.NotificationRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == requestId, ct);
            if (request is null)
                return Results.Json(new { code = "request_not_found", message = "Request not found." }, statusCode: 404);

            var deliveries = await db.NotificationDeliveries.AsNoTracking()
                .Where(d => d.RequestId == requestId)
                .Select(d => new
                {
                    d.UserId,
                    d.Status,
                    d.Channel,
                    d.AttemptCount,
                    d.DeliveredAt,
                    d.ReadAt,
                    d.AcknowledgedAt,
                    d.LastError,
                })
                .ToListAsync(ct);

            // Lifecycle timeline: Accepted -> Queued -> Processing -> Delivered/Failed.
            return Results.Ok(new
            {
                request.Id,
                request.ClientId,
                request.Priority,
                request.EventType,
                request.Status,
                request.AcceptedAt,
                request.QueuedAt,
                request.ProcessedAt,
                request.DeliveredAt,
                request.FailedAt,
                deliveries,
            });
        }).RequireAuthorization("AdminOnly");

        // ────────────────────────────────────────────────────────────────────────
        // Recipient inbox: the user's personal notification list (per-user aggregation
        // of their NotificationDelivery rows). Filtered by module type + read status,
        // paginated, newest first. This backs /notifications/inbox in Blazor.
        // ────────────────────────────────────────────────────────────────────────
        v1.MapGet("/inbox", async (string? type, string? status, int? page, int? pageSize,
            HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();

            var result = await ListInboxCore(db, userId.Value, type, status, page ?? 1, pageSize ?? 20, ct);
            return Results.Ok(result);
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Webhook callback HMAC signature builder (v1 stub). v2: POST delivery state changes to
    /// the request's WebhookUrl with header "X-IPop-Signature: sha256=...", signed with the
    /// client's rotating secret; the client verifies the payload before trusting it.
    /// </summary>
    internal static string SignWebhook(byte[] secretBytes, string payload)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Internal (read-only, admin) query cores — extracted so tests can exercise the exact
    // handler bodies against TestDb (same seam as DirectoryReadEndpointsTests). Shape = what
    // the frontend viewer/dashboard expects; never exposes SecretHash.

    internal static async Task<IReadOnlyList<GatewayClientListRow>> ListClientsCore(IPopDbContext db, CancellationToken ct)
        => await db.NotificationGatewayClients.AsNoTracking()
            .OrderBy(c => c.ClientId)
            .Select(c => new GatewayClientListRow(
                c.Id, c.ClientId, c.AllowedIps, c.AllowedEventTypes,
                c.RateLimitPerSecond, c.MaxRecipients, c.IsActive, c.CreatedAt, c.SecretRotatedAt,
                c.Requests.Count))
            .ToListAsync(ct);

    internal static async Task<GatewayRequestListResult> ListRequestsCore(
        IPopDbContext db, string? status, string? priority, string? clientId, int page, int pageSize, CancellationToken ct)
    {
        var p = Math.Max(1, page);
        var size = Math.Clamp(pageSize, 1, 100);
        var q = db.NotificationRequests.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status.ToLowerInvariant().Trim());
        if (!string.IsNullOrWhiteSpace(priority)) q = q.Where(r => r.Priority == priority.ToLowerInvariant().Trim());
        if (!string.IsNullOrWhiteSpace(clientId)) q = q.Where(r => r.Client.ClientId == clientId.Trim());

        var total = await q.CountAsync(ct);
        var byStatus = await db.NotificationRequests.AsNoTracking()
            .GroupBy(r => r.Status)
            .Select(g => new GatewayStatusCountRow(g.Key, g.Count()))
            .ToListAsync(ct);
        var items = await q
            .OrderByDescending(r => r.AcceptedAt)
            .Skip((p - 1) * size).Take(size)
            .Select(r => new GatewayRequestListRow(
                r.Id, r.Client.ClientId, r.EventType, r.Title, r.Priority, r.Status,
                r.AcceptedAt, r.DeliveredAt, r.FailedAt, r.Deliveries.Count))
            .ToListAsync(ct);
        return new GatewayRequestListResult(items, total, byStatus, p, size);
    }

    internal static async Task<GatewayRequestListResult> ListClientRequestsCore(
        IPopDbContext db, Guid clientId, int page, int pageSize, CancellationToken ct)
    {
        var p = Math.Max(1, page);
        var size = Math.Clamp(pageSize, 1, 100);
        var q = db.NotificationRequests.AsNoTracking().Where(r => r.ClientId == clientId);
        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(r => r.AcceptedAt)
            .Skip((p - 1) * size).Take(size)
            .Select(r => new GatewayRequestListRow(
                r.Id, r.Client.ClientId, r.EventType, r.Title, r.Priority, r.Status,
                r.AcceptedAt, r.DeliveredAt, r.FailedAt, r.Deliveries.Count))
            .ToListAsync(ct);
        return new GatewayRequestListResult(items, total, [], p, size);
    }

    /// <summary>
    /// Per-user inbox query. The delivery's request carries the event metadata (title, body,
    /// priority, event type); the delivery row carries the per-user state (read/acknowledged,
    /// channel, delivered time). Type is derived from the client's EventType so module filters
    /// (chat/mail/news/project/system) work uniformly even though event types are free-form.
    /// </summary>
    internal static async Task<InboxListResult> ListInboxCore(
        IPopDbContext db, Guid userId, string? type, string? status, int page, int pageSize, CancellationToken ct)
    {
        var p = Math.Max(1, page);
        var size = Math.Clamp(pageSize, 1, 100);

        var q = db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.UserId == userId);

        if (!string.IsNullOrWhiteSpace(type))
        {
            var t = type.Trim().ToLowerInvariant();
            // EF can't translate ClassifyEventType — express the same predicate with
            // string operations it supports (StartsWith), mirrored on the module naming
            // convention (chat.mention, mail.new, memo.ack, task.assigned...).
            q = t switch
            {
                "chat" => q.Where(d => d.Request.EventType.StartsWith("chat.")),
                "mail" => q.Where(d => d.Request.EventType.StartsWith("mail.")),
                "news" => q.Where(d => d.Request.EventType.StartsWith("news.") || d.Request.EventType.StartsWith("memo.")),
                "project" => q.Where(d => d.Request.EventType.StartsWith("project.") || d.Request.EventType.StartsWith("task.")),
                "system" => q.Where(d => !d.Request.EventType.StartsWith("chat.") && !d.Request.EventType.StartsWith("mail.") &&
                            !d.Request.EventType.StartsWith("news.") && !d.Request.EventType.StartsWith("memo.") &&
                            !d.Request.EventType.StartsWith("project.") && !d.Request.EventType.StartsWith("task.")),
                _ => q,
            };
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            // read = delivered/read/acknowledged; unread = everything that hasn't been read yet.
            q = s switch
            {
                "read" => q.Where(d => d.Status == NotificationStatus.Read || d.Status == NotificationStatus.Acknowledged),
                "unread" => q.Where(d => d.Status != NotificationStatus.Read && d.Status != NotificationStatus.Acknowledged),
                _ => q,
            };
        }

        var total = await q.CountAsync(ct);
        var unread = await db.NotificationDeliveries.AsNoTracking()
            .CountAsync(d => d.UserId == userId &&
                d.Status != NotificationStatus.Read &&
                d.Status != NotificationStatus.Acknowledged, ct);

        // Newest first: the per-recipient timestamp (DeliveredAt, else UpdatedAt) leads.
        // ClassifyEventType is not translatable to SQL — pull the raw event type and map in memory.
        var rows = await q
            .OrderByDescending(d => d.DeliveredAt ?? d.UpdatedAt)
            .Skip((p - 1) * size).Take(size)
            .Select(d => new
            {
                d.RequestId,
                d.Request.EventType,
                d.Request.Title,
                d.Request.Body,
                d.Request.Priority,
                d.Request.TargetUrl,
                Source = d.Request.Client.ClientId,
                d.DeliveredAt,
                d.UpdatedAt,
                d.Status,
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new InboxItemRow(
            r.RequestId,
            ClassifyEventType(r.EventType),
            r.Title,
            r.Body,
            r.Source,
            r.DeliveredAt ?? r.UpdatedAt,
            r.Status == NotificationStatus.Read || r.Status == NotificationStatus.Acknowledged,
            r.Priority,
            r.TargetUrl)).ToList();

        return new InboxListResult(items, total, unread, p, size);
    }

    /// <summary>Maps a client EventType to a module kind for the inbox filter: chat/mail/news/project/system.</summary>
    internal static string ClassifyEventType(string eventType)
    {
        var e = eventType.ToLowerInvariant();
        if (e.StartsWith("chat.")) return "chat";
        if (e.StartsWith("mail.")) return "mail";
        if (e.StartsWith("news.") || e.StartsWith("memo.")) return "news";
        if (e.StartsWith("project.") || e.StartsWith("task.")) return "project";
        return "system";
    }

    // ── Query DTOs (AdminOnly read shapes — never carries secret material) ──────

    public record GatewayClientListRow(
        Guid Id, string ClientId, string? AllowedIps, string? AllowedEventTypes,
        int RateLimitPerSecond, int MaxRecipients, bool IsActive, DateTime CreatedAt,
        DateTime? SecretRotatedAt, int RequestCount);

    public record GatewayStatusCountRow(string Status, int Count);

    public record GatewayRequestListRow(
        Guid Id, string ClientId, string EventType, string Title, string Priority, string Status,
        DateTime AcceptedAt, DateTime? DeliveredAt, DateTime? FailedAt, int DeliveryCount);

    public record GatewayRequestListResult(
        IReadOnlyList<GatewayRequestListRow> Items, int Total,
        IReadOnlyList<GatewayStatusCountRow> ByStatus, int Page, int PageSize);

    // Per-user inbox shape — matches what the Blazor inbox page renders.
    public record InboxItemRow(
        Guid Id, string Type, string Title, string Body,
        string Source, DateTime At, bool IsRead, string Priority,
        string? TargetUrl = null);

    public record InboxListResult(
        IReadOnlyList<InboxItemRow> Items, int TotalCount,
        int UnreadCount, int Page, int PageSize);

    // Preferensi notifikasi (Fase 4)
    public record NotificationPreferenceDto(bool QuietHoursEnabled, string? QuietStart, string? QuietEnd, string? MutedTypes, string? QuietDays, bool AllowImportant, string? TimeZoneId = null);

    internal static async Task<NotificationGatewayClientInfo?> AuthenticateRetryClientAsync(
        HttpContext http, INotificationGatewayService svc, CancellationToken ct)
    {
        var clientId = http.Request.Headers["X-Client-Id"].FirstOrDefault();
        var secret = http.Request.Headers["X-Client-Secret"].FirstOrDefault();
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret)) return null;
        return await svc.AuthenticateClientAsync(clientId, secret, http.Connection.RemoteIpAddress?.ToString(), ct);
    }

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    private static async Task<bool> TryTakeRateTicketAsync(Guid clientId, int perSecond, CancellationToken ct)
    {
        if (perSecond <= 0) return true; // 0 = unlimited
        var bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await RateLock.WaitAsync(ct);
        try
        {
            if (!RateBuckets.TryGetValue(clientId, out var rb))
            {
                RateBuckets[clientId] = rb = new RateBucket { WindowStartSec = bucket, Count = 0 };
            }
            if (rb.WindowStartSec != bucket)
            {
                rb.WindowStartSec = bucket;
                rb.Count = 0;
                // Opportunistic cleanup: drop stale entries (bounded by client count).
                if (RateBuckets.Count > 512)
                    foreach (var k in RateBuckets.Where(e => e.Value.WindowStartSec < bucket - 2).Select(e => e.Key).ToList())
                        RateBuckets.Remove(k);
            }
            if (rb.Count >= perSecond) return false;
            rb.Count++;
            return true;
        }
        finally { RateLock.Release(); }
    }

    // Request DTOs
    public record RegisterClientRequest(
        string ClientId,
        string Secret,
        string? AllowedIps = null,
        string? AllowedEventTypes = null,
        int RateLimitPerSecond = 0,
        int MaxRecipients = 1000);

    public record RetryRequest(string UserNik);
    public record TimezoneRequest(string TimeZoneId);
}
