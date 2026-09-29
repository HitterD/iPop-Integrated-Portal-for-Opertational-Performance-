using IPop.Application.Notifications;
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
    }

    /// Logika satu tick (dipisah agar bisa diuji unit): terbitkan notifikasi untuk
    /// pengingat yang jatuh tempo, lalu tandai SentAt. Mengembalikan jumlah terkirim.
    public static async Task<int> ProcessDueRemindersAsync(IPopDbContext db, INotificationGatewayService gateway, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await db.MeetingReminders
            .Where(r => r.SentAt == null && r.SendAt <= now)
            .Join(db.Meetings.Where(m => m.Status != "dibatalkan"), r => r.MeetingId, m => m.Id, (r, m) => new { r.Id, r.UserId, r.SendAt, Meeting = m })
            .Take(200)
            .ToListAsync(ct);
        if (due.Count == 0) return 0;

        var sent = 0;
        foreach (var row in due)
        {
            // Terbitkan; tandai SentAt HANYA bila publish berhasil — kegagalan mencoba
            // lagi tick berikutnya, tidak menandai terkirim palsu.
            var result = await gateway.PublishInternalAsync(
                clientSlug: "meetings",
                eventType: "meeting.reminder",
                title: $"Meeting Reminder: {row.Meeting.Title}",
                message: $"Meeting \"{row.Meeting.Title}\" starts at {row.Meeting.StartAt.ToLocalTime():dd MMM yyyy HH:mm}.",
                target: new NotificationTarget(UserId: row.UserId),
                priority: "high",
                targetUrl: $"/calls?tab=scheduled&meeting={row.Meeting.Id}",
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
