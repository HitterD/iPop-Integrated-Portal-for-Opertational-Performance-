using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Application.Tickets;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IPop.Infrastructure.Tickets;

/// ICT tickets through iDesk (docs/specs/idesk-tickets.md). The local mirror answers every read;
/// iDesk is the source of truth and is consulted by sync, reconcile, retries and its webhook.
/// A new ticket is stored as SENDING with its externalRef before the POST, so a timeout never
/// loses it and a resend can't create a duplicate (iDesk is idempotent on externalRef).
public sealed class TicketService(
    IPopDbContext db,
    IIdeskClient idesk,
    TicketModuleCache modules,
    INotificationGatewayService notify,
    IRealtime realtime,
    ILogger<TicketService>? logger = null,
    IdeskCalendarSync? calendar = null,
    IPop.Infrastructure.Notifications.Push.IDesktopToastSink? desktop = null) : ITicketService
{
    public const string Provider = "idesk";
    public const string OriginIpop = "IPOP";
    public const string OriginIdesk = "IDESK";
    public const string HardwareInstallation = "HARDWARE_INSTALLATION";
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);
    private static readonly string[] Priorities = ["LOW", "MEDIUM", "HIGH", "CRITICAL"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger _log = logger ?? NullLogger<TicketService>.Instance;

    // ── Modules ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TicketModuleDto>?> GetModulesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var cached = modules.Get(now, out var fresh);
        if (fresh) return cached;
        var r = await idesk.GetModulesAsync(ct);
        if (!r.Ok) return cached;   // stale beats nothing while iDesk is down
        modules.Set(r.Value!, now);
        return r.Value;
    }

    private string ModuleName(string slug, string? fallback = null) =>
        modules.Get(DateTime.UtcNow, out _)?.FirstOrDefault(m => m.Slug == slug)?.Name ?? fallback ?? slug;

    // ── Reads ───────────────────────────────────────────────────────────

    public async Task<TicketListDto> ListAsync(Guid userId, bool openOnly, CancellationToken ct = default)
    {
        var q = db.Tickets.AsNoTracking().Where(t => t.UserId == userId && t.RemovedAt == null);
        if (openOnly) q = q.Where(t => t.Status != TicketStatus.Closed && t.Status != TicketStatus.Cancelled);
        var rows = await q.OrderByDescending(t => t.CreatedAt).Take(500).ToListAsync(ct);
        var state = await db.TicketUserStates.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        return new TicketListDto(rows.Select(ToListItem).ToList(), state?.SyncedAt, state?.Offline ?? false, state?.NotRegistered ?? false);
    }

    public async Task<TicketDetailDto?> GetAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId && x.RemovedAt == null, ct);
        return t is null ? null : await ToDetailAsync(t, ct);
    }

    /// The ticket's public chat from iDesk, read-only. Opening it clears the unread counter. When iDesk
    /// can't be reached the last preview we received comes back with Offline set.
    public async Task<TicketMessagesDto?> GetMessagesAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId && x.RemovedAt == null, ct);
        if (t is null) return null;
        var unread = t.UnreadMessages;
        if (unread > 0)
        {
            t.UnreadMessages = 0;
            await db.SaveChangesAsync(ct);
            await ChangedAsync(userId, null, null, ct);
        }
        // Reading the chat here also clears its toast / Inbox notifications.
        if (await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Nik).FirstOrDefaultAsync(ct) is { } readerNik)
            await ClearChatNotificationsAsync(userId, readerNik, t.IdeskTicketId, t.Id, ct);
        if (t.TicketNumber is null || IsPending(t.Status)) return new TicketMessagesDto([], unread, HttpIdeskClient.PublicLink(t.Url), false, null);

        var r = await idesk.GetMessagesAsync(t.TicketNumber, ct);
        if (r.Ok)
            return new TicketMessagesDto(r.Value!.Messages.Select(m => new TicketMessageDto(m.Id,
                m.FromRequester ? null : m.SenderName, m.FromRequester, m.Content, m.Attachments, m.CreatedAt)).ToList(),
                unread, r.Value.Url ?? HttpIdeskClient.PublicLink(t.Url), false, null);
        var last = t.LastMessagePreview is null ? null
            : new TicketMessageDto("last", t.LastMessageSender, false, t.LastMessagePreview, [], t.LastMessageAt ?? t.UpdatedAt);
        return new TicketMessagesDto([], unread, HttpIdeskClient.PublicLink(t.Url), true, last);
    }

    // ── Sync / reconcile (silent: realtime only, no notifications) ──────

    public async Task<TicketListDto> SyncAsync(Guid userId, bool openOnly, CancellationToken ct = default)
    {
        var nik = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Nik).FirstOrDefaultAsync(ct);
        if (nik is null) return await ListAsync(userId, openOnly, ct);

        var state = await db.TicketUserStates.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (state is null) db.TicketUserStates.Add(state = new TicketUserState { UserId = userId });

        // Both go to iDesk; side by side so a cold module cache doesn't add a second round trip.
        var warm = GetModulesAsync(ct);   // ModuleName for new rows
        var list = idesk.ListByNikAsync(nik, "all", ct);
        await Task.WhenAll(warm, list);
        var r = await list;
        var changed = false;
        switch (r.Outcome)
        {
            case IdeskOutcome.Ok:
                changed = await MergeListAsync(userId, r.Value!, ct);
                state.SyncedAt = DateTime.UtcNow; state.Offline = false; state.NotRegistered = false;
                break;
            case IdeskOutcome.NotRegistered:
                state.NotRegistered = true; state.Offline = false;
                break;
            default:
                state.Offline = true;
                break;
        }
        await db.SaveChangesAsync(ct);
        if (changed) await ChangedAsync(userId, nik, null, ct);
        return await ListAsync(userId, openOnly, ct);
    }

    /// Upserts the full ?state=all answer and soft-deletes what iDesk no longer has. iDesk caps the
    /// list at 100 newest, so with a full page only tickets inside the returned window can be judged.
    private async Task<bool> MergeListAsync(Guid userId, IReadOnlyList<IdeskTicket> remote, CancellationToken ct)
    {
        var mine = await db.Tickets.Where(t => t.UserId == userId).ToListAsync(ct);
        var ids = remote.Select(x => x.TicketId).ToList();
        var refs = remote.Where(x => x.ExternalRef != null).Select(x => x.ExternalRef!).ToList();
        // Rows matched by id/ref but filed under someone else are left alone.
        var others = await db.Tickets.Where(t => t.UserId != userId
            && ((t.IdeskTicketId != null && ids.Contains(t.IdeskTicketId)) || (t.ExternalRef != null && refs.Contains(t.ExternalRef))))
            .Select(t => new { t.IdeskTicketId, t.ExternalRef }).ToListAsync(ct);

        var changed = false;
        var seen = new HashSet<Guid>();
        foreach (var it in remote)
        {
            if (others.Any(o => o.IdeskTicketId == it.TicketId || (it.ExternalRef != null && o.ExternalRef == it.ExternalRef))) continue;
            var row = mine.FirstOrDefault(t => t.IdeskTicketId == it.TicketId)
                      ?? (it.ExternalRef is null ? null : mine.FirstOrDefault(t => t.ExternalRef == it.ExternalRef));
            if (row is null)
            {
                row = NewMirror(userId, it);
                db.Tickets.Add(row); mine.Add(row);
                changed = true;
            }
            else if (IsPending(row.Status))
            {
                Land(row, it); changed = true;
            }
            else changed |= Apply(row, it);
            seen.Add(row.Id);
            if (calendar is not null) await calendar.SyncVisitAsync(row, ct);
        }

        var windowStart = remote.Count >= 100 ? remote.Min(x => x.CreatedAt) : DateTime.MinValue;
        foreach (var gone in mine.Where(t => t.RemovedAt == null && t.IdeskTicketId != null && !IsPending(t.Status)
                     && !seen.Contains(t.Id) && t.CreatedAt >= windowStart))
        {
            gone.RemovedAt = DateTime.UtcNow; changed = true;
            if (calendar is not null) await calendar.SyncVisitAsync(gone, ct);
        }
        return changed;
    }

    public Task<bool> RefreshAsync(Guid id, CancellationToken ct = default) => RefreshAsync(id, TimeSpan.Zero, ct);

    /// Re-reads one ticket unless it was refreshed within <paramref name="minAge"/>.
    public async Task<bool> RefreshAsync(Guid id, TimeSpan minAge, CancellationToken ct)
    {
        var row = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null || row.TicketNumber is null || row.RemovedAt != null || IsPending(row.Status)) return false;
        var now = DateTime.UtcNow;
        if (row.RefreshedAt is { } at && now - at < minAge) return false;

        var r = await idesk.GetTicketAsync(row.TicketNumber, ct);
        var changed = false;
        if (r.Ok) changed = Apply(row, r.Value!);
        else if (r.Outcome == IdeskOutcome.NotFound) { row.RemovedAt = now; changed = true; }
        else return false;   // unreachable: try again next time
        row.RefreshedAt = now;
        var visitMoved = calendar is not null && await calendar.SyncVisitAsync(row, ct);
        await db.SaveChangesAsync(ct);
        if (visitMoved) await CalendarChangedAsync(row.UserId, null, ct);
        if (changed) await ChangedAsync(row.UserId, null, row.Id, ct);
        return changed;
    }

    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        if (!idesk.IsConfigured) return 0;
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var ids = await db.Tickets.AsNoTracking()
            .Where(t => t.RemovedAt == null && t.TicketNumber != null
                && t.Status != TicketStatus.Closed && t.Status != TicketStatus.Cancelled
                && t.Status != TicketStatus.Sending && t.Status != TicketStatus.Failed
                && (t.RefreshedAt == null || t.RefreshedAt < cutoff))
            .OrderBy(t => t.RefreshedAt).Select(t => t.Id).Take(200).ToListAsync(ct);
        var changed = 0;
        foreach (var id in ids)
            if (await RefreshAsync(id, TimeSpan.Zero, ct)) changed++;
        return changed;
    }

    // ── Create / retry / discard ────────────────────────────────────────

    public async Task<TicketCreateOutcome> CreateAsync(Guid userId, CreateTicketRequest req, CancellationToken ct = default)
    {
        if (!idesk.IsConfigured) return new(TicketCreateKind.NotConfigured, Message: "iDesk is not configured on this server.");
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Nik }).FirstOrDefaultAsync(ct);
        if (user is null) return new(TicketCreateKind.Invalid, Message: "Unknown user.");

        var mods = await GetModulesAsync(ct);
        var v = Validate(req, mods, DateOnly.FromDateTime(DateTime.Now));
        if (v.Error is { } err) return new(TicketCreateKind.Invalid, Message: err.Message, Field: err.Field);

        var now = DateTime.UtcNow;
        var row = new Ticket
        {
            Id = Guid.NewGuid(), UserId = userId, Provider = Provider, Origin = OriginIpop,
            ExternalRef = await NextExternalRefAsync(DateTime.Now.Year, ct),
            Status = TicketStatus.Sending, Priority = req.Priority, Module = req.Module,
            ModuleName = mods?.FirstOrDefault(m => m.Slug == req.Module)?.Name ?? req.Module,
            Category = v.Category ?? "", Title = req.Title.Trim(), Description = req.Description.Trim(),
            Device = Clean(req.Device), Software = Clean(req.Software),
            CriticalReason = req.Priority == "CRITICAL" ? Clean(req.CriticalReason) : null,
            HardwareType = v.Category == HardwareInstallation ? Clean(req.HardwareType) : null,
            ScheduledDate = v.Category == HardwareInstallation ? req.ScheduledDate : null,
            ScheduledTime = v.Category == HardwareInstallation ? req.ScheduledTime : null,
            CreatedAt = now, UpdatedAt = now,
            AttachmentsJson = v.Attachments.Count == 0 ? null
                : JsonSerializer.Serialize(v.Attachments.Select(a => new TicketAttachmentDto(a.FileName, a.Size)), Json),
        };
        row.PendingPayload = JsonSerializer.Serialize(new
        {
            externalRef = row.ExternalRef, module = row.Module, category = v.Category, priority = row.Priority,
            title = row.Title, description = row.Description, requester = new { nik = user.Nik },
            criticalReason = row.CriticalReason, device = row.Device, software = row.Software,
            hardwareType = row.HardwareType, scheduledDate = row.ScheduledDate, scheduledTime = row.ScheduledTime,
            attachments = v.Attachments.Count == 0 ? null : v.Attachments.Select(a => new { fileName = a.FileName, contentBase64 = a.Base64 }),
        }, Json);
        db.Tickets.Add(row);
        if (calendar is not null) await calendar.SyncVisitAsync(row, ct);   // the picked slot shows at once
        await db.SaveChangesAsync(ct);

        var r = await idesk.CreateTicketAsync(row.PendingPayload, ct);
        switch (r.Outcome)
        {
            case IdeskOutcome.Ok:
                Land(row, r.Value!);
                AddEvent(row, "created", null, row.Status, row.AssigneeName, now);
                await SetRegisteredAsync(userId, ct);
                if (calendar is not null) await calendar.SyncVisitAsync(row, ct);
                await db.SaveChangesAsync(ct);
                await ChangedAsync(userId, user.Nik, row.Id, ct);
                return new(TicketCreateKind.Created, await ToDetailAsync(row, ct));

            case IdeskOutcome.Rejected or IdeskOutcome.NotRegistered or IdeskOutcome.NotFound:
                // Nothing is kept: the form shows the error and the user fixes or abandons it.
                row.RemovedAt = now;
                if (calendar is not null) await calendar.SyncVisitAsync(row, ct);
                db.Tickets.Remove(row);
                if (r.Outcome == IdeskOutcome.NotRegistered) await MarkNotRegisteredAsync(userId, ct);
                await db.SaveChangesAsync(ct);
                return r.Outcome == IdeskOutcome.NotRegistered
                    ? new(TicketCreateKind.NotRegistered, Message: NotRegisteredMessage)
                    : new(TicketCreateKind.Rejected, Message: r.Message ?? "iDesk rejected the ticket.");

            default:
                // Timeout / 5xx / network: keep it as SENDING, the outbox resends the same body.
                row.SendAttempts = 1;
                row.NextAttemptAt = now + Backoff(1);
                row.LastError = r.Message;
                await db.SaveChangesAsync(ct);
                await ChangedAsync(userId, user.Nik, row.Id, ct);
                return new(TicketCreateKind.Queued, await ToDetailAsync(row, ct));
        }
    }

    public const string NotRegisteredMessage =
        "Your iDesk account isn't set up yet. Contact ICT to create your iDesk account so you can submit tickets and follow their progress.";

    public async Task<TicketRetryOutcome> RetryAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var row = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId && t.RemovedAt == null, ct);
        if (row is null) return new(TicketRetryKind.NotFound);
        if (!IsPending(row.Status) || row.PendingPayload is null) return new(TicketRetryKind.NotFailed);
        if (!idesk.IsConfigured) return new(TicketRetryKind.NotConfigured, Message: "iDesk is not configured on this server.");

        var now = DateTime.UtcNow;
        var r = await idesk.CreateTicketAsync(row.PendingPayload, ct);
        row.SendAttempts++;
        TicketRetryOutcome result;
        switch (r.Outcome)
        {
            case IdeskOutcome.Ok:
                Land(row, r.Value!);
                AddEvent(row, "sent", null, row.Status, row.AssigneeName, now);
                await SetRegisteredAsync(userId, ct);
                await db.SaveChangesAsync(ct);
                await ChangedAsync(userId, null, row.Id, ct);
                return new(TicketRetryKind.Sent, await ToDetailAsync(row, ct));
            case IdeskOutcome.NotRegistered:
                await MarkNotRegisteredAsync(userId, ct);
                row.LastError = NotRegisteredMessage;
                result = new(TicketRetryKind.NotRegistered, Message: NotRegisteredMessage);
                break;
            case IdeskOutcome.Rejected or IdeskOutcome.NotFound:
                row.Status = TicketStatus.Failed; row.NextAttemptAt = null;
                row.LastError = r.Message ?? "iDesk rejected the ticket.";
                result = new(TicketRetryKind.Rejected, Message: row.LastError);
                break;
            default:
                row.LastError = r.Message;
                result = new(TicketRetryKind.Unreachable, Message: "iDesk can't be reached right now. Try again in a few minutes.");
                break;
        }
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await ChangedAsync(userId, null, row.Id, ct);
        return result;
    }

    public async Task<TicketDiscardKind> DiscardAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var row = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId && t.RemovedAt == null, ct);
        if (row is null) return TicketDiscardKind.NotFound;
        if (!IsPending(row.Status)) return TicketDiscardKind.NotPending;
        row.RemovedAt = DateTime.UtcNow;
        if (calendar is not null) await calendar.SyncVisitAsync(row, ct);
        db.Tickets.Remove(row);   // never reached iDesk as far as we know; nothing worth keeping
        await db.SaveChangesAsync(ct);
        await ChangedAsync(userId, null, id, ct);
        return TicketDiscardKind.Discarded;
    }

    // ── Outbox ──────────────────────────────────────────────────────────

    /// Backoff after the n-th failed attempt: 1, 2, 5, 10, 30 min, then every 30 min.
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(attempts switch
    {
        <= 1 => 1, 2 => 2, 3 => 5, 4 => 10, _ => 30,
    });

    public async Task<int> ProcessOutboxAsync(DateTime now, CancellationToken ct = default)
    {
        if (!idesk.IsConfigured) return 0;
        var due = await db.Tickets
            .Where(t => t.Status == TicketStatus.Sending && t.RemovedAt == null && (t.NextAttemptAt == null || t.NextAttemptAt <= now))
            .OrderBy(t => t.NextAttemptAt).Take(50).ToListAsync(ct);
        foreach (var row in due)
        {
            // Only the outbox may move SENDING on; a webhook or retry may have landed it meanwhile.
            await db.Entry(row).ReloadAsync(ct);
            if (row.Status != TicketStatus.Sending) continue;

            if (now - row.CreatedAt >= GiveUpAfter)
            {
                await FailAsync(row, row.LastError ?? "iDesk could not be reached for 24 hours.", now, ct);
                continue;
            }

            var r = await idesk.CreateTicketAsync(row.PendingPayload ?? "{}", ct);
            row.SendAttempts++;
            switch (r.Outcome)
            {
                case IdeskOutcome.Ok:
                    Land(row, r.Value!);
                    AddEvent(row, "sent", null, row.Status, row.AssigneeName, now);
                    await SetRegisteredAsync(row.UserId, ct);
                    await db.SaveChangesAsync(ct);
                    await NotifyAsync(row, "ticket.sent", "Ticket sent to ICT", ct);
                    await ChangedAsync(row.UserId, null, row.Id, ct);
                    break;
                case IdeskOutcome.NotRegistered:
                    await MarkNotRegisteredAsync(row.UserId, ct);
                    await FailAsync(row, NotRegisteredMessage, now, ct);
                    break;
                case IdeskOutcome.Rejected or IdeskOutcome.NotFound:
                    await FailAsync(row, r.Message ?? "iDesk rejected the ticket.", now, ct);
                    break;
                default:
                    row.LastError = r.Message;
                    row.NextAttemptAt = now + Backoff(row.SendAttempts);
                    await db.SaveChangesAsync(ct);
                    break;
            }
        }
        return due.Count;
    }

    private async Task FailAsync(Ticket row, string error, DateTime now, CancellationToken ct)
    {
        row.Status = TicketStatus.Failed;
        row.NextAttemptAt = null;
        row.LastError = error.Length > 1000 ? error[..1000] : error;
        row.UpdatedAt = now;
        AddEvent(row, "send_failed", TicketStatus.Sending, TicketStatus.Failed, null, now);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(row, "ticket.send_failed", "Couldn't send your ticket", ct);
        await ChangedAsync(row.UserId, null, row.Id, ct);
    }

    // ── Webhook ─────────────────────────────────────────────────────────

    public async Task<TicketWebhookKind> ApplyWebhookAsync(string body, CancellationToken ct = default)
    {
        string? eventId, evt; DateTime occurredAt; string? previousStatus; IdeskTicket? it;
        IdeskBooking? booking = null; string? cancelledBooking = null; WebhookMessage? message = null;
        List<string>? recipients = null; string? reader = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            eventId = Prop(root, "id");
            evt = Prop(root, "event");
            previousStatus = Prop(root, "previousStatus");
            occurredAt = Prop(root, "occurredAt") is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : DateTime.UtcNow;
            it = root.TryGetProperty("ticket", out var t) ? HttpIdeskClient.ParseTicket(t) : null;
            if (root.TryGetProperty("booking", out var b) && b.ValueKind == JsonValueKind.Object)
            {
                booking = HttpIdeskClient.ParseBooking(b);
                cancelledBooking = Prop(b, "bookingId");
            }
            if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object)
                message = new WebhookMessage(
                    m.TryGetProperty("sender", out var snd) && snd.ValueKind == JsonValueKind.Object ? Prop(snd, "name") : null,
                    Prop(m, "preview") ?? "",
                    m.TryGetProperty("fromRequester", out var fr) && fr.ValueKind == JsonValueKind.True,
                    m.TryGetProperty("attachments", out var att) && att.ValueKind == JsonValueKind.Array
                        ? att.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object)
                            .Select(a => new WebhookAttachment(Prop(a, "name") ?? "", Prop(a, "kind") ?? "file")).ToList()
                        : []);
            // Older iDesk builds send no recipients: their chat event always meant "for the requester".
            recipients = root.TryGetProperty("recipients", out var rc) && rc.ValueKind == JsonValueKind.Array
                ? rc.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).Select(r => Prop(r, "nik")).OfType<string>().ToList()
                : null;
            reader = root.TryGetProperty("reader", out var rd) && rd.ValueKind == JsonValueKind.Object ? Prop(rd, "nik") : null;
        }
        catch (JsonException) { return TicketWebhookKind.Invalid; }
        if (string.IsNullOrEmpty(eventId) || string.IsNullOrEmpty(evt)) return TicketWebhookKind.Invalid;
        if (eventId.Length > 100) eventId = eventId[..100];

        if (evt.StartsWith("zoom_booking.", StringComparison.Ordinal))
            return await ApplyBookingWebhookAsync(eventId, evt, booking, cancelledBooking, ct);
        if (it is null) return TicketWebhookKind.Invalid;

        if (await db.TicketEvents.AnyAsync(e => e.WebhookEventId == eventId, ct)) return TicketWebhookKind.Duplicate;

        // Opened in iDesk: clear that person's chat notifications for the ticket (no timeline line).
        if (evt == "ticket.read")
        {
            if (!await FirstReceiptAsync(eventId, ct)) return TicketWebhookKind.Duplicate;
            if (reader is not null) await ClearChatNotificationsAsync(reader, it, ct);
            return TicketWebhookKind.Applied;
        }
        // A chat message for someone other than the requester (the assigned agent): they have no mirror of the
        // ticket, so the notification points at the ticket in iDesk. The requester's own copy is handled below.
        if (evt == "ticket.message_created" && message is not null && recipients is not null)
        {
            // These lines leave no timeline row (no mirror), so the receipt table catches redeliveries.
            if (recipients.Any(n => n != it.RequesterNik) && !await FirstReceiptAsync("agent:" + eventId, ct)) return TicketWebhookKind.Duplicate;
            foreach (var agentNik in recipients.Where(n => n != it.RequesterNik).Distinct())
                await NotifyAgentReplyAsync(agentNik, it, message, eventId, ct);
            if (!recipients.Contains(it.RequesterNik ?? "\0")) return TicketWebhookKind.Applied;
        }

        var row = (it.ExternalRef is null ? null : await db.Tickets.FirstOrDefaultAsync(x => x.ExternalRef == it.ExternalRef, ct))
                  ?? await db.Tickets.FirstOrDefaultAsync(x => x.IdeskTicketId == it.TicketId, ct);
        string? nik = null;
        var notifyQueuedLanding = false;
        if (row is null)
        {
            var owner = it.RequesterNik is null ? null
                : await db.Users.AsNoTracking().Where(u => u.Nik == it.RequesterNik).Select(u => new { u.Id, u.Nik }).FirstOrDefaultAsync(ct);
            if (owner is null)
            {
                _log.LogDebug("iDesk webhook {Event} for a requester without an iPop account ignored", evt);
                return TicketWebhookKind.UnknownRequester;
            }
            row = NewMirror(owner.Id, it);
            nik = owner.Nik;
            db.Tickets.Add(row);
        }
        else if (IsPending(row.Status))
        {
            // The webhook beat our POST answer or the outbox: this is the landing.
            notifyQueuedLanding = row.SendAttempts > 0;
            Land(row, it);
            if (notifyQueuedLanding) AddEvent(row, "sent", null, row.Status, row.AssigneeName, DateTime.UtcNow);
        }
        else if (it.UpdatedAt >= row.UpdatedAt) Apply(row, it);   // a late redelivery must not roll the mirror back
        row.RemovedAt = null;

        var kind = evt.StartsWith("ticket.", StringComparison.Ordinal) ? evt["ticket.".Length..] : evt;
        if (kind == "message_created" && message is not null)
        {
            row.UnreadMessages++;
            var preview = ChatPreview(message);
            row.LastMessagePreview = preview.Length > 200 ? preview[..200] : preview;
            row.LastMessageSender = message.Sender is { Length: > 200 } sender ? sender[..200] : message.Sender;
            row.LastMessageAt = occurredAt;
        }
        var visitMoved = calendar is not null && await calendar.SyncVisitAsync(row, ct);
        // ticket.created for a ticket we filed ourselves: the timeline already says so; just
        // stamp the event id on that line so a redelivery is still recognised.
        var own = kind == "created" && db.Entry(row).State != EntityState.Added
            ? await db.TicketEvents.FirstOrDefaultAsync(e => e.TicketId == row.Id && e.WebhookEventId == null
                && (e.Kind == "created" || e.Kind == "sent"), ct)
            : null;
        if (own is not null) own.WebhookEventId = eventId;
        else db.TicketEvents.Add(new TicketEvent
        {
            Id = Guid.NewGuid(), TicketId = row.Id, Kind = kind.Length > 32 ? kind[..32] : kind,
            FromStatus = kind is "assigned" or "message_created" or "visit_changed" ? null : previousStatus, ToStatus = it.Status,
            Assignee = kind == "message_created" ? message?.Sender : it.AssigneeName, OccurredAt = occurredAt, WebhookEventId = eventId,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // A concurrent delivery of the same event won the unique index.
            db.ChangeTracker.Clear();
            return TicketWebhookKind.Duplicate;
        }

        if (notifyQueuedLanding) await NotifyAsync(row, "ticket.sent", "Ticket sent to ICT", ct);
        if (visitMoved) await CalendarChangedAsync(row.UserId, nik, ct);
        if (kind == "message_created")
        {
            await NotifyReplyAsync(row, ct);
            await ChangedAsync(row.UserId, nik, row.Id, ct);
            return TicketWebhookKind.Applied;
        }
        var title = kind switch
        {
            "assigned" when it.AssigneeName is { Length: > 0 } a => $"{a} picked up your ticket",
            "assigned" => "Your ticket was assigned",
            "resolved" => "Ticket resolved — please confirm",
            "closed" => "Ticket closed",
            "status_changed" => $"Ticket status: {HumanizeStatus(it.Status)}",
            "visit_changed" when VisitLabel(row) is { } v => $"ICT visit moved to {v}",
            _ => null,   // ticket.created and anything unknown stay silent
        };
        if (title is not null) await NotifyAsync(row, evt, title, ct);
        await ChangedAsync(row.UserId, nik, row.Id, ct);
        return TicketWebhookKind.Applied;
    }

    // ── Mapping ─────────────────────────────────────────────────────────

    private static bool IsPending(string status) => status is TicketStatus.Sending or TicketStatus.Failed;

    private Ticket NewMirror(Guid userId, IdeskTicket it)
    {
        var now = DateTime.UtcNow;
        var row = new Ticket
        {
            Id = Guid.NewGuid(), UserId = userId, Provider = Provider,
            Origin = it.Origin == OriginIpop ? OriginIpop : OriginIdesk,
            ExternalRef = it.ExternalRef, Status = it.Status, Priority = it.Priority, Module = it.Module,
            ModuleName = ModuleName(it.Module), Category = it.Category ?? "", Title = it.Title,
            CreatedAt = it.CreatedAt, UpdatedAt = it.UpdatedAt, RefreshedAt = now,
        };
        Apply(row, it);
        return row;
    }

    /// iDesk accepted a SENDING/FAILED ticket: adopt its identity and drop the payload (attachments).
    private void Land(Ticket row, IdeskTicket it)
    {
        row.PendingPayload = null;
        row.NextAttemptAt = null;
        row.LastError = null;
        row.Status = it.Status;   // so Apply sees a real change from SENDING
        Apply(row, it);
        row.RefreshedAt = DateTime.UtcNow;
    }

    /// Copies iDesk's view onto the mirror. Returns true if anything the user can see changed.
    private bool Apply(Ticket row, IdeskTicket it)
    {
        var before = (row.Status, row.AssigneeName, row.TicketNumber, row.Title, row.Priority, row.Category, row.SlaTarget,
            row.ResolvedAt, row.ClosedAt, row.Url, row.IdeskTicketId, row.RemovedAt, row.VisitStart, row.VisitEnd);
        row.IdeskTicketId = it.TicketId;
        row.TicketNumber = it.TicketNumber ?? row.TicketNumber;
        row.Status = it.Status;
        row.Priority = it.Priority;
        if (row.Module != it.Module) { row.Module = it.Module; row.ModuleName = ModuleName(it.Module, row.ModuleName); }
        if (it.Category is not null) row.Category = it.Category;
        if (it.Title.Length > 0) row.Title = it.Title.Length > 200 ? it.Title[..200] : it.Title;
        row.AssigneeName = it.AssigneeName;
        row.SlaTarget = it.SlaTarget;
        if (it.Visit is { } visit) { row.VisitStart = visit.Start; row.VisitEnd = visit.End; }
        else if (it.Origin == OriginIdesk || row.VisitStart is not null) { row.VisitStart = null; row.VisitEnd = null; }
        row.ResolvedAt = it.ResolvedAt;
        row.ClosedAt = it.ClosedAt;
        row.Url = it.Url;
        row.UpdatedAt = it.UpdatedAt > row.UpdatedAt ? it.UpdatedAt : row.UpdatedAt;
        row.RemovedAt = null;
        return before != (row.Status, row.AssigneeName, row.TicketNumber, row.Title, row.Priority, row.Category, row.SlaTarget,
            row.ResolvedAt, row.ClosedAt, row.Url, row.IdeskTicketId, row.RemovedAt, row.VisitStart, row.VisitEnd);
    }

    private void AddEvent(Ticket row, string kind, string? from, string? to, string? assignee, DateTime at) =>
        db.TicketEvents.Add(new TicketEvent { Id = Guid.NewGuid(), TicketId = row.Id, Kind = kind, FromStatus = from, ToStatus = to, Assignee = assignee, OccurredAt = at });

    private static TicketListItemDto ToListItem(Ticket t) => new(t.Id, t.TicketNumber, t.ExternalRef, t.Origin, t.Status, t.Priority,
        t.Module, t.ModuleName, t.Category, t.Title, t.CreatedAt, t.UpdatedAt, t.UnreadMessages);

    private async Task<TicketDetailDto> ToDetailAsync(Ticket t, CancellationToken ct)
    {
        var events = await db.TicketEvents.AsNoTracking().Where(e => e.TicketId == t.Id).ToListAsync(ct);
        var attachments = string.IsNullOrEmpty(t.AttachmentsJson) ? []
            : JsonSerializer.Deserialize<List<TicketAttachmentDto>>(t.AttachmentsJson, Json) ?? [];
        return new TicketDetailDto(t.Id, t.TicketNumber, t.ExternalRef, t.Origin, t.Status, t.Priority, t.Module, t.ModuleName,
            t.Category, t.Title, t.Description, t.Device, t.Software, t.CriticalReason, t.HardwareType, t.ScheduledDate, t.ScheduledTime,
            t.AssigneeName is null ? null : new TicketPersonDto(t.AssigneeName), t.SlaTarget, t.CreatedAt, t.UpdatedAt,
            t.ResolvedAt, t.ClosedAt, HttpIdeskClient.PublicLink(t.Url), t.LastError, attachments,
            events.OrderBy(e => e.OccurredAt).Select(e => new TicketEventDto(e.Kind, e.FromStatus, e.ToStatus, e.Assignee, e.OccurredAt)).ToList(),
            IdeskCalendarSync.VisitOf(t) is { } v ? new TicketVisitDto(v.Start, v.End) : null, t.UnreadMessages);
    }

    /// "IN_PROGRESS" → "In progress", "WAITING_VENDOR" → "Waiting vendor".
    public static string HumanizeStatus(string code)
    {
        var s = code.Replace('_', ' ').ToLowerInvariant();
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── External ref ────────────────────────────────────────────────────

    /// IPOP-ICT-{yyyy}-{NNNNNN}. The UPDATE takes the year row's lock until commit, so two submits
    /// never read the same number; a missing year row is inserted, and a lost insert race retries.
    public async Task<string> NextExternalRefAsync(int year, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var updated = await db.TicketRefCounters.Where(c => c.Year == year)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Last, c => c.Last + 1), ct);
                if (updated == 0)
                {
                    var counter = new TicketRefCounter { Year = year, Last = 1 };
                    db.TicketRefCounters.Add(counter);
                    try { await db.SaveChangesAsync(ct); }
                    finally { db.Entry(counter).State = EntityState.Detached; }
                }
                var last = await db.TicketRefCounters.AsNoTracking().Where(c => c.Year == year).Select(c => c.Last).SingleAsync(ct);
                await tx.CommitAsync(ct);
                return $"IPOP-ICT-{year:D4}-{last:D6}";
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                await tx.RollbackAsync(ct);
            }
        }
    }

    // ── Validation (mirrors iDesk so the form fails fast) ───────────────

    public sealed record ValidAttachment(string FileName, string Base64, long Size);
    public sealed record ValidationError(string Field, string Message);
    public sealed record Validation(ValidationError? Error, string? Category, IReadOnlyList<ValidAttachment> Attachments);

    private static Validation Fail(string field, string message) => new(new ValidationError(field, message), null, []);

    /// <paramref name="modules"/> may be null when iDesk is down and nothing is cached; the module and
    /// category checks are then left to iDesk when the outbox delivers the ticket.
    public static Validation Validate(CreateTicketRequest req, IReadOnlyList<TicketModuleDto>? modules, DateOnly today)
    {
        var title = req.Title?.Trim() ?? "";
        var description = req.Description?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(req.Module)) return Fail("module", "Choose a queue.");
        var module = modules?.FirstOrDefault(m => m.Slug == req.Module);
        if (modules is not null && module is null) return Fail("module", "That queue is not available.");
        var priorities = module?.Priorities is { Count: > 0 } p ? p : Priorities;
        if (!priorities.Contains(req.Priority)) return Fail("priority", "Choose a priority.");
        if (title.Length is < 5 or > 200) return Fail("title", "Title must be 5–200 characters.");
        if (description.Length is < 3 or > 5000) return Fail("description", "Description must be 3–5000 characters.");
        if (req.Priority == "CRITICAL" && (req.CriticalReason?.Trim().Length ?? 0) is < 3 or > 500)
            return Fail("criticalReason", "Tell ICT why this is critical (3–500 characters).");
        if ((req.Device?.Trim().Length ?? 0) > 100) return Fail("device", "Device must be at most 100 characters.");
        if ((req.Software?.Trim().Length ?? 0) > 100) return Fail("software", "Software must be at most 100 characters.");

        var category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim();
        if (module is not null)
        {
            if (category is null)
                category = module.DefaultCategory ?? (module.Categories.Count == 1 ? module.Categories[0] : null);
            if (category is null && module.Categories.Count > 0) return Fail("category", "Choose a category.");
            if (category is not null && module.Categories.Count > 0 && !module.Categories.Contains(category))
                return Fail("category", "That category doesn't belong to this queue.");
        }

        if (category == HardwareInstallation)
        {
            var hw = req.HardwareType?.Trim() ?? "";
            if (hw.Length is < 1 or > 100) return Fail("hardwareType", "Choose the hardware to install.");
            if (!DateOnly.TryParseExact(req.ScheduledDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return Fail("scheduledDate", "Choose an installation date.");
            if (date < today) return Fail("scheduledDate", "The installation date can't be in the past.");
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return Fail("scheduledDate", "Installations happen on weekdays.");
            if (req.ScheduledTime is null || !TicketAttachmentRules.HardwareSlots.Contains(req.ScheduledTime))
                return Fail("scheduledTime", "Choose one of the available time slots.");
        }

        var files = req.Attachments ?? [];
        if (files.Count > TicketAttachmentRules.MaxFiles) return Fail("attachments", $"Attach at most {TicketAttachmentRules.MaxFiles} files.");
        var valid = new List<ValidAttachment>(files.Count);
        foreach (var f in files)
        {
            var name = Path.GetFileName((f.FileName ?? "").Replace('\\', '/').Split('/').Last()).Trim();
            if (name.Length is 0 or > 255) return Fail("attachments", "Every attachment needs a file name.");
            if (!TicketAttachmentRules.Extensions.Contains(Path.GetExtension(name)))
                return Fail("attachments", $"{name}: this file type can't be attached.");
            var b64 = f.ContentBase64 ?? "";
            var comma = b64.StartsWith("data:", StringComparison.Ordinal) ? b64.IndexOf(',') : -1;
            if (comma >= 0) b64 = b64[(comma + 1)..];   // tolerate a data: URL prefix
            byte[] bytes;
            try { bytes = Convert.FromBase64String(b64); }
            catch (FormatException) { return Fail("attachments", $"{name}: the file couldn't be read."); }
            if (bytes.Length == 0) return Fail("attachments", $"{name} is empty.");
            if (bytes.Length > TicketAttachmentRules.MaxBytes) return Fail("attachments", $"{name} is larger than 10 MB.");
            valid.Add(new ValidAttachment(name, b64, bytes.Length));
        }
        return new Validation(null, category, valid);
    }

    // ── Side effects ────────────────────────────────────────────────────

    private async Task SetRegisteredAsync(Guid userId, CancellationToken ct)
    {
        var s = await db.TicketUserStates.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (s is { NotRegistered: true }) s.NotRegistered = false;
    }

    private async Task MarkNotRegisteredAsync(Guid userId, CancellationToken ct)
    {
        var s = await db.TicketUserStates.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (s is null) db.TicketUserStates.Add(new TicketUserState { UserId = userId, NotRegistered = true });
        else s.NotRegistered = true;
    }

    /// Records a processed webhook id that has no timeline line of its own; false when it was seen before.
    private async Task<bool> FirstReceiptAsync(string eventId, CancellationToken ct)
    {
        if (eventId.Length > 100) eventId = eventId[..100];
        if (await db.IdeskWebhookReceipts.AnyAsync(r => r.EventId == eventId, ct)) return false;
        db.IdeskWebhookReceipts.Add(new IdeskWebhookReceipt { EventId = eventId, ReceivedAt = DateTime.UtcNow });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }
    }

    private sealed record WebhookAttachment(string Name, string Kind);
    private sealed record WebhookMessage(string? Sender, string Preview, bool FromRequester = false, List<WebhookAttachment>? Attachments = null);

    /// The preview shown in notifications: the text, or what was attached ("📎 Foto", "📎 Video", "📎 laporan.pdf").
    private static string ChatPreview(WebhookMessage m)
    {
        var files = m.Attachments ?? [];
        var hasText = !string.IsNullOrWhiteSpace(m.Preview) && !m.Preview.StartsWith("📎 ", StringComparison.Ordinal);
        if (files.Count == 0) return m.Preview;
        var label = files.Count > 1 ? $"📎 {files.Count} lampiran"
            : files[0].Kind switch { "image" => "📎 Foto", "video" => "📎 Video", _ => "📎 " + files[0].Name };
        return hasText ? $"{label} · {m.Preview}" : label;
    }

    /// Prefix of the notification link for a ticket in iDesk (agent side). Its own scheme so the Notifier and the
    /// read clean-up can recognise it; the web resolves it to the public iDesk link.
    public const string IdeskTargetPrefix = "/idesk/tickets/";

    /// Chat message for the assigned agent (by NIK): one notification per ticket, replaced like the requester's.
    private async Task NotifyAgentReplyAsync(string nik, IdeskTicket it, WebhookMessage message, string eventId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Nik == nik && u.IsActive).Select(u => new { u.Id }).FirstOrDefaultAsync(ct);
        if (user is null) return;
        var url = IdeskTargetPrefix + it.TicketId;
        var unread = 1;
        try
        {
            var stale = await db.NotificationDeliveries
                .Where(d => d.UserId == user.Id && d.ReadAt == null && d.Request.TargetUrl == url && d.Request.EventType == ReplyEvent)
                .ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var d in stale) { d.Status = NotificationStatus.Read; d.ReadAt = now; d.UpdatedAt = now; }
            if (stale.Count > 0) await db.SaveChangesAsync(ct);
            unread += stale.Count;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Clearing older agent reply notifications failed (non-fatal)"); }

        var number = it.TicketNumber ?? "a ticket";
        var title = unread > 1 ? $"{unread} new messages on {number}" : $"New message on {number}";
        var who = string.IsNullOrWhiteSpace(message.Sender) ? it.RequesterName ?? "Requester" : message.Sender;
        var body = $"{it.Title}\n{who}: “{ChatPreview(message)}”";
        try
        {
            await notify.PublishInternalAsync(clientSlug: "tickets", eventType: ReplyEvent, title: title,
                message: body, target: new NotificationTarget(UserId: user.Id), priority: "normal", targetUrl: url, ct: ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Agent ticket reply notification failed ({EventId})", eventId); }
    }

    /// The ticket was opened (in iPop or iDesk): its unread chat notifications for that person are read.
    private async Task ClearChatNotificationsAsync(string nik, IdeskTicket it, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => new { u.Id }).FirstOrDefaultAsync(ct);
        if (user is null) return;
        var mirror = await db.Tickets.AsNoTracking().Where(x => x.IdeskTicketId == it.TicketId && x.UserId == user.Id)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        await ClearChatNotificationsAsync(user.Id, nik, it.TicketId, mirror, ct);
    }

    private async Task ClearChatNotificationsAsync(Guid userId, string nik, string? ideskTicketId, Guid? mirrorId, CancellationToken ct)
    {
        var urls = new List<string>();
        if (ideskTicketId is not null) urls.Add(IdeskTargetPrefix + ideskTicketId);
        if (mirrorId is { } m) urls.Add($"/tickets/{m}#messages");
        if (urls.Count == 0) return;
        // The Notifier hides the ticket's toast even when nothing was unread any more (e.g. read in another tab).
        if (desktop is not null)
        {
            var refs = new List<string>();
            if (ideskTicketId is not null) refs.Add(ideskTicketId);
            if (mirrorId is { } mid) refs.Add(mid.ToString());
            try { await desktop.TicketChatReadAsync(userId, refs, ct); } catch (Exception ex) { _log.LogDebug(ex, "Ticket toast clean-up failed"); }
        }
        try
        {
            var open = await db.NotificationDeliveries
                .Where(d => d.UserId == userId && d.ReadAt == null && d.Request.EventType == ReplyEvent && urls.Contains(d.Request.TargetUrl!))
                .ToListAsync(ct);
            if (open.Count == 0) return;
            var now = DateTime.UtcNow;
            foreach (var d in open) { d.Status = NotificationStatus.Read; d.ReadAt = now; d.UpdatedAt = now; }
            await db.SaveChangesAsync(ct);
            await realtime.ToUserAsync(nik, IPop.Application.Realtime.RealtimeEvents.NotificationsChanged, ct: ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Clearing ticket chat notifications failed (non-fatal)"); }
    }

    /// Text reply typed in the iPop Notifier, posted to iDesk as this user. The requester replies on their own
    /// ticket (mirror id); the assigned agent on an iDesk ticket id. iDesk enforces who may reply.
    public async Task<TicketReplyOutcome> ReplyAsync(Guid userId, string ticketRef, string content, CancellationToken ct = default)
    {
        var text = (content ?? "").Trim();
        if (text.Length == 0) return new(TicketReplyKind.Invalid, "Type a message first.");
        if (text.Length > 4000) return new(TicketReplyKind.Invalid, "That message is too long (4000 characters at most).");
        var nik = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Nik).FirstOrDefaultAsync(ct);
        if (nik is null) return new(TicketReplyKind.NotFound, null);

        string? remote = null; Guid? mirrorId = null; string? ideskId = null;
        if (Guid.TryParse(ticketRef, out var mid) && await db.Tickets.AsNoTracking()
                .Where(x => x.Id == mid && x.UserId == userId && x.RemovedAt == null)
                .Select(x => new { x.Id, x.TicketNumber, x.IdeskTicketId, x.Status }).FirstOrDefaultAsync(ct) is { } mine)
        {
            if (mine.TicketNumber is null || IsPending(mine.Status)) return new(TicketReplyKind.NotSent, "This ticket hasn't reached iDesk yet.");
            remote = mine.TicketNumber; mirrorId = mine.Id; ideskId = mine.IdeskTicketId;
        }
        else if (Guid.TryParse(ticketRef, out _))
        {
            remote = ticketRef; ideskId = ticketRef;   // an iDesk ticket id (agent side)
        }
        if (remote is null) return new(TicketReplyKind.NotFound, null);

        var r = await idesk.ReplyAsync(remote, nik, text, ct);
        if (r.Ok) await ClearChatNotificationsAsync(userId, nik, ideskId, mirrorId, ct);
        return r.Outcome switch
        {
            IdeskOutcome.Ok => new(TicketReplyKind.Sent, null),
            IdeskOutcome.NotFound => new(TicketReplyKind.NotFound, null),
            IdeskOutcome.Rejected or IdeskOutcome.NotRegistered => new(TicketReplyKind.Rejected, r.Message),
            IdeskOutcome.NotConfigured => new(TicketReplyKind.NotConfigured, null),
            _ => new(TicketReplyKind.Unreachable, r.Message),
        };
    }

    /// "Tue 13 Oct, 09:00" (WIB) of the ticket's visit, for notifications.
    private static string? VisitLabel(Ticket row) =>
        IdeskCalendarSync.VisitOf(row) is { } v ? (v.Start + TimeSpan.FromHours(7)).ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture) : null;

    /// One notification per ticket: earlier unread reply notifications for this ticket are cleared first,
    /// so the bell keeps only the newest (with the running count) and the toast replaces itself.
    private async Task NotifyReplyAsync(Ticket row, CancellationToken ct)
    {
        var url = $"/tickets/{row.Id}#messages";
        try
        {
            var stale = await db.NotificationDeliveries
                .Where(d => d.UserId == row.UserId && d.ReadAt == null && d.Request.TargetUrl == url && d.Request.EventType == ReplyEvent)
                .ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var d in stale) { d.Status = NotificationStatus.Read; d.ReadAt = now; d.UpdatedAt = now; }
            if (stale.Count > 0) await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Clearing older reply notifications failed (non-fatal)"); }

        var number = row.TicketNumber ?? row.ExternalRef ?? "your ticket";
        var title = row.UnreadMessages > 1 ? $"{row.UnreadMessages} new replies on {number}" : $"New reply on {number}";
        var who = string.IsNullOrWhiteSpace(row.LastMessageSender) ? "ICT" : row.LastMessageSender;
        var body = $"{row.Title}\n{who}: “{row.LastMessagePreview}”";
        try
        {
            await notify.PublishInternalAsync(clientSlug: "tickets", eventType: ReplyEvent, title: title,
                message: body, target: new NotificationTarget(UserId: row.UserId),
                priority: "normal", targetUrl: url, ct: ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Ticket reply notification failed"); }
    }

    public const string ReplyEvent = "ticket.message_created";

    private async Task<TicketWebhookKind> ApplyBookingWebhookAsync(string eventId, string evt, IdeskBooking? booking, string? bookingId, CancellationToken ct)
    {
        if (calendar is null) return TicketWebhookKind.Applied;   // accepted, nothing to mirror into
        // Booking events have no ticket timeline to hold their id; the processed-id table dedupes them.
        if (await db.IdeskWebhookReceipts.AnyAsync(r => r.EventId == eventId, ct)) return TicketWebhookKind.Duplicate;
        db.IdeskWebhookReceipts.Add(new IdeskWebhookReceipt { EventId = eventId, ReceivedAt = DateTime.UtcNow });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return TicketWebhookKind.Duplicate; }

        switch (evt)
        {
            case "zoom_booking.created" or "zoom_booking.updated" when booking is not null:
                await calendar.ApplyBookingAsync(booking, evt == "zoom_booking.updated", ct);
                return TicketWebhookKind.Applied;
            case "zoom_booking.cancelled" when !string.IsNullOrEmpty(bookingId):
                await calendar.CancelBookingAsync(bookingId, ct);
                return TicketWebhookKind.Applied;
            default:
                return TicketWebhookKind.Invalid;
        }
    }

    private async Task CalendarChangedAsync(Guid userId, string? nik, CancellationToken ct)
    {
        try
        {
            nik ??= await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Nik).FirstOrDefaultAsync(ct);
            if (nik is not null) await realtime.ToUserAsync(nik, RealtimeEvents.CalendarChanged, null, ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Calendar realtime hint failed"); }
    }

    private async Task NotifyAsync(Ticket row, string eventType, string title, CancellationToken ct)
    {
        try
        {
            await notify.PublishInternalAsync(clientSlug: "tickets", eventType: eventType, title: title,
                message: row.Title, target: new NotificationTarget(UserId: row.UserId),
                priority: "normal", targetUrl: $"/tickets/{row.Id}", ct: ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Ticket notification {Event} failed", eventType); }
    }

    /// "tickets.changed" { id } (id null = the list as a whole) plus the nav badge count.
    private async Task ChangedAsync(Guid userId, string? nik, Guid? id, CancellationToken ct)
    {
        try
        {
            nik ??= await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Nik).FirstOrDefaultAsync(ct);
            if (nik is null) return;
            await realtime.ToUserAsync(nik, TicketEvents.Changed, new { id }, ct);
            var resolved = await db.Tickets.AsNoTracking()
                .CountAsync(t => t.UserId == userId && t.RemovedAt == null && t.Status == TicketStatus.Resolved, ct);
            await realtime.ToUserAsync(nik, RealtimeEvents.BadgeChanged, new RealtimeArgs(TicketEvents.BadgeKey, resolved), ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Ticket realtime push failed"); }
    }
}
