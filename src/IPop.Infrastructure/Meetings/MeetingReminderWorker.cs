using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Meetings;

/// Penjadwal pengingat rapat (Fase 4 — Pengingat Real-time). Tiap menit memindai
/// MeetingReminder yang belum terkirim dan sudah jatuh waktu (SendAt <= now), lalu
/// menerbitkan notifikasi toast real-time ke peserta via gerbang notifikasi.
/// Anti duplikat: SentAt ditandai setelah publish berhasil.
public sealed class MeetingReminderWorker(IServiceScopeFactory scopeFactory, ILogger<MeetingReminderWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Tick(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "MeetingReminderWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var gateway = scope.ServiceProvider.GetService<INotificationGatewayService>();
        if (gateway is null) return;

        await ProcessDueRemindersAsync(db, gateway, ct);
        await ProcessDueEventRemindersAsync(db, gateway, ct);
    }

    /// Pengingat event kalender pribadi (tanpa rapat): satu notifikasi ke pemilik,
    /// ReminderSentAt mencegah duplikat. Event yang sudah lewat dilewati.
    public static async Task<int> ProcessDueEventRemindersAsync(IPopDbContext db, INotificationGatewayService gateway, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var candidates = await db.CalendarEvents
            .Where(e => e.MeetingId == null && e.ReminderMinutes != null && e.ReminderSentAt == null && e.StartAt > now)
            .OrderBy(e => e.StartAt).Take(500).ToListAsync(ct);
        var sent = 0;
        foreach (var e in candidates.Where(e => e.StartAt.AddMinutes(-e.ReminderMinutes!.Value) <= now).Take(200))
        {
            var result = await gateway.PublishInternalAsync(
                clientSlug: "meetings",
                eventType: "calendar.reminder",
                title: $"Reminder: {e.Title}",
                message: $"\"{e.Title}\" starts at {e.StartAt.ToLocalTime():dd MMM yyyy HH:mm}.",
                target: new NotificationTarget(UserId: e.OwnerId),
                priority: "normal",
                targetUrl: $"/calendar?event={e.Id}&date={e.StartAt.ToLocalTime():yyyy-MM-dd}",
                ct: ct);
            if (result.Success) { e.ReminderSentAt = DateTime.UtcNow; sent++; }
        }
        if (sent > 0) await db.SaveChangesAsync(ct);
        return sent;
    }

    /// Logika satu tick (dipisah agar bisa diuji unit): terbitkan notifikasi untuk
    /// pengingat yang jatuh tempo, lalu tandai SentAt. Mengembalikan jumlah terkirim.
    public static async Task<int> ProcessDueRemindersAsync(IPopDbContext db, INotificationGatewayService gateway, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await db.MeetingReminders
            .Where(r => r.SentAt == null && r.SendAt <= now)
            .Join(db.Meetings.Where(m => m.Status != MeetingStatus.Cancelled), r => r.MeetingId, m => m.Id, (r, m) => new { r.Id, r.UserId, r.SendAt, Meeting = m })
            .Take(200)
            .ToListAsync(ct);
        if (due.Count == 0) return 0;

        // Recipients who still belong to the meeting: an attendee who has not declined and,
        // for a room meeting, is still an active member of that room. Anyone else (removed,
        // left the room) and any reminder whose meeting already started is retired unsent —
        // a worker outage must not flood people with reminders for meetings in the past.
        var meetingIds = due.Select(r => r.Meeting.Id).Distinct().ToList();
        var attendees = await db.MeetingAttendees.AsNoTracking()
            .Where(a => meetingIds.Contains(a.MeetingId) && a.RsvpStatus != "decline")
            .Select(a => new { a.MeetingId, a.UserId }).ToListAsync(ct);
        var attending = attendees.Select(a => (a.MeetingId, a.UserId)).ToHashSet();
        var roomIds = due.Where(r => r.Meeting.RoomId != null).Select(r => r.Meeting.RoomId!.Value).Distinct().ToList();
        var members = (await db.ChatRoomMembers.AsNoTracking()
            .Where(m => roomIds.Contains(m.RoomId) && m.LeftAt == null)
            .Select(m => new { m.RoomId, m.UserId }).ToListAsync(ct))
            .Select(m => (m.RoomId, m.UserId)).ToHashSet();

        var sent = 0;
        foreach (var row in due)
        {
            var stillInvited = (row.UserId == row.Meeting.OrganizerId || attending.Contains((row.Meeting.Id, row.UserId)))
                && (row.Meeting.RoomId is not { } roomId || members.Contains((roomId, row.UserId)));
            if (row.Meeting.StartAt <= now || !stillInvited)
            {
                var stale = await db.MeetingReminders.FirstOrDefaultAsync(r => r.Id == row.Id, ct);
                if (stale is not null) stale.SentAt = now;
                continue;
            }

            // Terbitkan; tandai SentAt HANYA bila publish berhasil — kegagalan mencoba
            // lagi tick berikutnya, tidak menandai terkirim palsu.
            var result = await gateway.PublishInternalAsync(
                clientSlug: "meetings",
                eventType: "meeting.reminder",
                title: $"Meeting Reminder: {row.Meeting.Title}",
                message: $"Meeting \"{row.Meeting.Title}\" starts at {row.Meeting.StartAt.ToLocalTime():dd MMM yyyy HH:mm}.",
                target: new NotificationTarget(UserId: row.UserId),
                priority: "high",
                // Open the meeting itself on the person's calendar (their own row when they have one).
                targetUrl: IPop.Application.Hall.HallBooking.RoomKey(row.Meeting.Location) is { } roomKey
                    ? $"/calls?tab=rooms&join={roomKey}"
                    : await db.CalendarEvents.AsNoTracking().Where(e => e.MeetingId == row.Meeting.Id)
                        .OrderBy(e => e.OwnerId == row.UserId ? 0 : 1).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct) is { } evId
                    ? $"/calendar?event={evId}&date={row.Meeting.StartAt.ToLocalTime():yyyy-MM-dd}"
                    : $"/calls?tab=scheduled&meeting={row.Meeting.Id}",
                ct: ct);

            if (result.Success)
            {
                var entity = await db.MeetingReminders.FirstOrDefaultAsync(r => r.Id == row.Id, ct);
                if (entity is not null) { entity.SentAt = DateTime.UtcNow; sent++; }
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }
}
