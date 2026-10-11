using System.Globalization;
using System.Text.Json;
using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Application.Tickets;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IPop.Infrastructure.Tickets;

/// Mirrors what is booked in iDesk onto iPop calendars as read-only events: Zoom bookings on the calendar
/// of every attendee with an iPop account, and the ICT visit of a hardware installation on the requester's.
/// Rows are ordinary <see cref="CalendarEvent"/>s (Normal visibility), so free/busy, Find a time, reminders
/// and the access ladder apply unchanged. NIKs are only used here to find users; rows and DTOs carry names.
public sealed class IdeskCalendarSync(
    IPopDbContext db,
    IIdeskClient idesk,
    INotificationGatewayService? notify,
    IRealtime? realtime,
    ILogger<IdeskCalendarSync>? logger = null)
{
    public const int DefaultReminderMinutes = 15;
    /// The visit slot iDesk books when it reports no end.
    public static readonly TimeSpan DefaultVisitLength = TimeSpan.FromHours(1);
    /// iDesk books in WIB (UTC+7, no DST).
    private static readonly TimeSpan Wib = TimeSpan.FromHours(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ILogger _log = logger ?? NullLogger<IdeskCalendarSync>.Instance;

    /// What a mirrored entry shows as its people (names only).
    public sealed record ExternalPerson(string Name, Guid? UserId, bool Booker);

    public static IReadOnlyList<ExternalPerson> People(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<ExternalPerson>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    // ── Zoom bookings ────────────────────────────────────────────────────

    /// zoom_booking.created / .updated: one row per attendee with an iPop account; attendees dropped from
    /// the booking lose it. Invited people (not the booker) hear about new invitations and moves.
    public async Task ApplyBookingAsync(IdeskBooking b, bool isUpdate, CancellationToken ct = default)
    {
        var niks = b.Attendees.Where(a => !string.IsNullOrEmpty(a.Nik)).Select(a => a.Nik!).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => niks.Contains(u.Nik) && u.IsActive)
            .Select(u => new { u.Id, u.Nik }).ToListAsync(ct);
        var idOf = users.ToDictionary(u => u.Nik, u => u.Id);
        var people = b.Attendees
            .Select(a => new ExternalPerson(a.Name, a.Nik is { } n && idOf.TryGetValue(n, out var id) ? id : null, a.Role == "BOOKER"))
            .ToList();
        var bookerId = people.FirstOrDefault(p => p.Booker)?.UserId;
        var owners = people.Where(p => p.UserId is not null).Select(p => p.UserId!.Value).Distinct().ToHashSet();

        var rows = await db.CalendarEvents
            .Where(e => e.Source == CalendarSources.IdeskZoom && e.ExternalId == b.BookingId).ToListAsync(ct);
        var invited = new List<Guid>();
        var moved = new List<Guid>();
        var dropped = rows.Where(r => !owners.Contains(r.OwnerId)).ToList();
        db.CalendarEvents.RemoveRange(dropped);
        foreach (var owner in owners)
        {
            var row = rows.FirstOrDefault(r => r.OwnerId == owner);
            if (row is null)
            {
                row = NewRow(owner, CalendarSources.IdeskZoom, b.BookingId);
                db.CalendarEvents.Add(row);
                if (owner != bookerId && !await SameSeriesNotifiedAsync(owner, b, ct)) invited.Add(owner);
            }
            else if (row.StartAt != b.Start || row.EndAt != b.End)
            {
                if (owner != bookerId) moved.Add(owner);
            }
            FillBooking(row, b, people);
        }
        await db.SaveChangesAsync(ct);

        var when = Local(b.Start);
        var url = (Guid owner) => $"/calendar?date={when:yyyy-MM-dd}";
        var booker = people.FirstOrDefault(p => p.Booker)?.Name ?? "Someone";
        await PublishAsync("calendar.idesk_zoom_invited", $"{booker} invited you to a Zoom meeting",
            $"{b.Title} · {when:ddd d MMM, HH:mm}. Added to your calendar.", invited, url(Guid.Empty), ct);
        if (isUpdate)
            await PublishAsync("calendar.idesk_zoom_moved", "Zoom meeting moved",
                $"{b.Title} · now {when:ddd d MMM, HH:mm}.", moved, url(Guid.Empty), ct);
        await DroppedAsync(dropped, bookerId, ct);
        await ChangedAsync(owners.Concat(dropped.Select(d => d.OwnerId)), ct);
    }

    /// zoom_booking.cancelled: removes the entry from every calendar that holds it.
    public async Task CancelBookingAsync(string bookingId, CancellationToken ct = default)
    {
        var rows = await db.CalendarEvents
            .Where(e => e.Source == CalendarSources.IdeskZoom && e.ExternalId == bookingId).ToListAsync(ct);
        if (rows.Count == 0) return;
        var bookerId = People(rows[0].ExternalPeople).FirstOrDefault(p => p.Booker)?.UserId;
        db.CalendarEvents.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        await DroppedAsync(rows, bookerId, ct);
        await ChangedAsync(rows.Select(r => r.OwnerId), ct);
    }

    /// Full resync of one user's Zoom bookings in [from, to] (WIB dates): what iDesk lists replaces what
    /// we hold in that window, so missed webhooks and cancellations are caught. Silent (no notifications).
    /// Returns false when iDesk could not answer.
    public async Task<bool> ResyncUserAsync(Guid userId, string nik, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var r = await idesk.ListZoomBookingsAsync(nik, from, to, ct);
        if (!r.Ok) return false;
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - Wib;
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - Wib;
        var held = await db.CalendarEvents
            .Where(e => e.OwnerId == userId && e.Source == CalendarSources.IdeskZoom && e.StartAt >= start && e.StartAt < end)
            .ToListAsync(ct);
        var listed = r.Value!.ToDictionary(b => b.BookingId);
        var changed = false;
        foreach (var gone in held.Where(h => !listed.ContainsKey(h.ExternalId!)))
        {
            db.CalendarEvents.Remove(gone); changed = true;
        }
        foreach (var b in listed.Values)
        {
            var row = held.FirstOrDefault(h => h.ExternalId == b.BookingId)
                ?? await db.CalendarEvents.FirstOrDefaultAsync(e => e.OwnerId == userId && e.Source == CalendarSources.IdeskZoom && e.ExternalId == b.BookingId, ct);
            var people = await PeopleAsync(b, ct);
            if (row is null) { db.CalendarEvents.Add(row = NewRow(userId, CalendarSources.IdeskZoom, b.BookingId)); changed = true; }
            var before = (row.Title, row.StartAt, row.EndAt, row.Link, row.Notes, row.ExternalPeople);
            FillBooking(row, b, people);
            changed |= before != (row.Title, row.StartAt, row.EndAt, row.Link, row.Notes, row.ExternalPeople);
        }
        if (!changed) return true;
        await db.SaveChangesAsync(ct);
        await ChangedAsync([userId], ct);
        return true;
    }

    // ── ICT visits ───────────────────────────────────────────────────────

    /// Keeps the requester's visit entry in line with the ticket (call before saving the ticket).
    /// A cancelled or removed ticket loses it; a closed one keeps it as history.
    public async Task<bool> SyncVisitAsync(Ticket ticket, CancellationToken ct = default)
    {
        var key = ticket.IdeskTicketId ?? ticket.Id.ToString();
        var row = await db.CalendarEvents.FirstOrDefaultAsync(e => e.OwnerId == ticket.UserId
            && e.Source == CalendarSources.IdeskVisit && (e.ExternalId == key || e.ExternalId == ticket.Id.ToString()), ct);
        var visit = VisitOf(ticket);
        if (visit is null || ticket.Status == TicketStatus.Cancelled || ticket.RemovedAt is not null)
        {
            if (row is null) return false;
            db.CalendarEvents.Remove(row);
            return true;
        }
        if (row is null) db.CalendarEvents.Add(row = NewRow(ticket.UserId, CalendarSources.IdeskVisit, key));
        var before = (row.Title, row.StartAt, row.EndAt, row.Notes, row.ExternalUrl, row.ExternalId);
        row.ExternalId = key;
        row.Title = Trim(string.IsNullOrWhiteSpace(ticket.HardwareType) ? "ICT visit" : $"{Humanize(ticket.HardwareType)} installation", 300);
        if (row.StartAt != visit.Value.Start) row.ReminderSentAt = null;
        row.StartAt = visit.Value.Start;
        row.EndAt = visit.Value.End;
        row.Location = "ICT visit";
        row.Notes = Trim(string.Join("\n", new[]
        {
            ticket.TicketNumber is null ? null : $"Ticket {ticket.TicketNumber} · {ticket.Title}",
            ticket.AssigneeName is null ? null : $"Assigned to {ticket.AssigneeName}",
        }.OfType<string>()), 4000);
        row.ExternalUrl = $"/tickets/{ticket.Id}";
        return before != (row.Title, row.StartAt, row.EndAt, row.Notes, row.ExternalUrl, row.ExternalId);
    }

    /// iDesk's visit, else the slot picked in iPop (date + "HH:mm", one hour, WIB) until iDesk reports one.
    public static (DateTime Start, DateTime End)? VisitOf(Ticket t)
    {
        if (t.VisitStart is { } s && t.VisitEnd is { } e && e > s) return (s, e);
        if (t.Category != TicketService.HardwareInstallation || t.ScheduledDate is null || t.ScheduledTime is null) return null;
        if (!DateOnly.TryParseExact(t.ScheduledDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            || !TimeOnly.TryParseExact(t.ScheduledTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return null;
        var start = d.ToDateTime(time, DateTimeKind.Utc) - Wib;
        return (start, start + DefaultVisitLength);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static CalendarEvent NewRow(Guid owner, string source, string externalId) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, Title = "", Type = "rapat", Source = source, ExternalId = externalId,
        Visibility = CalendarVisibility.Busy, ReminderMinutes = DefaultReminderMinutes, CreatedAt = DateTime.UtcNow,
    };

    private static void FillBooking(CalendarEvent row, IdeskBooking b, IReadOnlyList<ExternalPerson> people)
    {
        if (row.StartAt != b.Start) row.ReminderSentAt = null;
        row.Title = Trim(b.Title, 300);
        row.StartAt = b.Start;
        row.EndAt = b.End;
        row.Location = Trim(b.AccountName is null ? "Zoom" : $"Zoom · {b.AccountName}", 300);
        row.Link = Trim(b.JoinUrl, 1000);
        row.Notes = Trim(string.Join("\n", new[]
        {
            string.IsNullOrWhiteSpace(b.Description) ? null : b.Description.Trim(),
            b.MeetingId is null ? null : $"Meeting ID {b.MeetingId}" + (b.Passcode is null ? "" : $" · Passcode {b.Passcode}"),
        }.OfType<string>()), 4000);
        row.ExternalUrl = Trim(b.Url, 500);
        row.ExternalPeople = JsonSerializer.Serialize(people, Json);
    }

    private async Task<IReadOnlyList<ExternalPerson>> PeopleAsync(IdeskBooking b, CancellationToken ct)
    {
        var niks = b.Attendees.Where(a => !string.IsNullOrEmpty(a.Nik)).Select(a => a.Nik!).Distinct().ToList();
        var idOf = await db.Users.AsNoTracking().Where(u => niks.Contains(u.Nik) && u.IsActive)
            .ToDictionaryAsync(u => u.Nik, u => u.Id, ct);
        return b.Attendees.Select(a => new ExternalPerson(a.Name, a.Nik is { } n && idOf.TryGetValue(n, out var id) ? id : null, a.Role == "BOOKER")).ToList();
    }

    /// Recurring bookings arrive as one created event per occurrence: one invitation per series is enough.
    private async Task<bool> SameSeriesNotifiedAsync(Guid owner, IdeskBooking b, CancellationToken ct)
    {
        if (b.SeriesId is null) return false;
        var since = DateTime.UtcNow.AddMinutes(-30);
        return await db.CalendarEvents.AsNoTracking().AnyAsync(e => e.OwnerId == owner && e.Source == CalendarSources.IdeskZoom
            && e.ExternalId != b.BookingId && e.Title == b.Title && e.CreatedAt >= since, ct)
            || db.CalendarEvents.Local.Any(e => e.OwnerId == owner && e.Source == CalendarSources.IdeskZoom && e.ExternalId != b.BookingId && e.Title == b.Title);
    }

    private async Task DroppedAsync(IReadOnlyList<CalendarEvent> rows, Guid? bookerId, CancellationToken ct)
    {
        foreach (var group in rows.Where(r => r.OwnerId != bookerId && r.EndAt > DateTime.UtcNow).GroupBy(r => r.Title))
        {
            var first = group.OrderBy(r => r.StartAt).First();
            await PublishAsync("calendar.idesk_zoom_cancelled", "Zoom meeting cancelled",
                $"{first.Title} · {Local(first.StartAt):ddd d MMM, HH:mm} — removed from your calendar.",
                group.Select(r => r.OwnerId).Distinct().ToList(), "/calendar", ct);
        }
    }

    private async Task PublishAsync(string eventType, string title, string message, IReadOnlyList<Guid> users, string url, CancellationToken ct)
    {
        if (notify is null || users.Count == 0) return;
        try
        {
            await notify.PublishInternalAsync("meetings", eventType, title, message,
                new NotificationTarget(UserIds: users.ToList()), "normal", url, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "iDesk calendar notification {Event} failed", eventType); }
    }

    private async Task ChangedAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        if (realtime is null) return;
        try
        {
            var ids = userIds.Distinct().ToList();
            var niks = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => u.Nik).ToListAsync(ct);
            if (niks.Count > 0) await realtime.ToUsersAsync(niks, RealtimeEvents.CalendarChanged, ct: ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Calendar realtime hint failed"); }
    }

    private static DateTime Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc) + Wib;

    private static string Humanize(string code)
    {
        var s = code.Replace('_', ' ').Trim();
        if (s.Length <= 3) return s.ToUpperInvariant();
        s = s.ToLowerInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(s))]
    private static string? Trim(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}
