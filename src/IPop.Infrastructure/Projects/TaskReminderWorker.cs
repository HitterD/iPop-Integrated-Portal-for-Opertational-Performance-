using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Projects;

/// Penjadwal pengingat tenggat tugas (Fase 2 — Diskusi Tugas). Tiap menit memindai
/// ProjectTaskReminder yang belum terkirim dan sudah jatuh waktu (DueDate - OffsetHours),
/// lalu menerbitkan notifikasi ke penanggung jawab via gerbang notifikasi.
/// Anti duplikat: SentAt ditandai sebelum publish; baris yang sudah terkirim tak pernah
/// diproses ulang, dan kegagalan publish tidak menggandakan kiriman (ditandai ulang
/// hanya setelah publish berhasil).
public sealed class TaskReminderWorker(IServiceScopeFactory scopeFactory, ILogger<TaskReminderWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Tick(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "TaskReminderWorker tick failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var gateway = scope.ServiceProvider.GetService<INotificationGatewayService>();
        if (gateway is null) return;

        var now = DateTime.UtcNow;
        // Reminder yang jatuh tempo: DueDate - OffsetHours <= now, belum terkirim,
        // tugas belum selesai, dan punya DueDate. Perbandingan dievaluasi di memori
        // setelah filter kasar server-side (DateDiffMinute tidak portabel ke SQLite seam).
        var candidates = await db.ProjectTaskReminders
            .Where(r => r.SentAt == null)
            .Join(db.ProjectTasks.Where(t => t.DueDate != null && t.Status != "done"),
                  r => r.TaskId, t => t.Id, (r, t) => new { r.Id, r.OffsetHours, Task = t })
            .Take(500)
            .ToListAsync(ct);

        var due = candidates
            .Where(x => x.Task.DueDate!.Value.AddHours(-x.OffsetHours) <= now)
            .Take(100)
            .ToList();

        if (due.Count == 0) return;

        foreach (var row in due)
        {
            try
            {
                // Already past due (worker outage, or reminder set after the deadline): retire
                // it unsent instead of firing a stale "due on …" reminder.
                if (row.Task.DueDate!.Value <= now)
                {
                    var stale = await db.ProjectTaskReminders.FirstOrDefaultAsync(r => r.Id == row.Id, ct);
                    if (stale is not null) stale.SentAt = now;
                    continue;
                }

                // Only assignees who are still members of the project.
                var assignees = await db.ProjectTaskAssignees.AsNoTracking()
                    .Where(a => a.TaskId == row.Task.Id
                                && db.ProjectMembers.Any(m => m.ProjectId == row.Task.ProjectId && m.UserId == a.UserId))
                    .Select(a => a.UserId)
                    .ToListAsync(ct);
                if (assignees.Count == 0) continue;

                var projectName = await db.Projects.AsNoTracking()
                    .Where(p => p.Id == row.Task.ProjectId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "Project";

                foreach (var uid in assignees)
                {
                    await gateway.PublishInternalAsync(
                        clientSlug: "projects",
                        eventType: "project.task_due_reminder",
                        title: $"Due Date Reminder: {row.Task.Title}",
                        message: $"Task \"{row.Task.Title}\" in {projectName} is due on {row.Task.DueDate!.Value.ToLocalTime():dd MMM yyyy} (reminder {row.OffsetHours} hours before).",
                        target: new NotificationTarget(UserId: uid),
                        priority: "high",
                        targetUrl: $"/projects/{row.Task.ProjectId}?task={row.Task.Id}",
                        ct: ct);
                }

                // Tandai terkirim SETELAH publish berhasil — kegagalan mencoba lagi tick berikutnya.
                var entity = await db.ProjectTaskReminders.FirstOrDefaultAsync(r => r.Id == row.Id, ct);
                if (entity is not null) entity.SentAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to send reminder {ReminderId} for task {TaskId}", row.Id, row.Task.Id);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
