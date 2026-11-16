using IPop.Application.Notes;
using IPop.Application.Notifications;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notes;

/// <summary>
/// To-dos in a note that have a person and a due date remind that person the day before ("due tomorrow") and on the day
/// ("due today"), in the person's own time zone, once each. A reminder goes only if the person can still open the page,
/// the item is not done, and it has not been sent before; a page in Trash sends nothing. Checked every 10 minutes.
/// </summary>
public sealed class NotesReminderWorker(IServiceScopeFactory scopes, ILogger<NotesReminderWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var gateway = scope.ServiceProvider.GetService<INotificationGatewayService>();
                    if (gateway is null) continue;
                    var sent = await RunOnceAsync(scope.ServiceProvider.GetRequiredService<IPopDbContext>(),
                        scope.ServiceProvider.GetRequiredService<INoteService>(), gateway, DateTime.UtcNow, log, stoppingToken);
                    if (sent > 0) log.LogInformation("Notes reminders: sent {Count}", sent);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogWarning(ex, "Notes reminder pass failed; will retry"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal static TimeZoneInfo ResolveTimeZone(string? id)
    {
        foreach (var candidate in new[] { id, "Asia/Jakarta", "SE Asia Standard Time" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    /// One pass; returns how many reminders were sent.
    public static async Task<int> RunOnceAsync(IPopDbContext db, INoteService notes, INotificationGatewayService gateway,
        DateTime nowUtc, ILogger log, CancellationToken ct)
    {
        // Coarse window first (UTC date -2..+3 days covers every time zone); the exact local-day rule is applied in memory.
        var from = DateOnly.FromDateTime(nowUtc.AddDays(-2));
        var to = DateOnly.FromDateTime(nowUtc.AddDays(3));
        var rows = await db.NoteTodoDues
            .Where(x => !x.Done && x.AssigneeId != null && x.Due >= from && x.Due <= to && (x.DayBeforeSentAt == null || x.DaySentAt == null))
            .OrderBy(x => x.Due).ThenBy(x => x.PageId).ThenBy(x => x.ItemKey).Take(500).ToListAsync(ct);
        if (rows.Count == 0) return 0;

        var userIds = rows.Select(r => r.AssigneeId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id) && u.IsActive)
            .Select(u => new { u.Id, u.TimeZoneId }).ToDictionaryAsync(u => u.Id, ct);
        var pageIds = rows.Select(r => r.PageId).Distinct().ToList();
        var pages = await db.NotePages.AsNoTracking().Where(p => pageIds.Contains(p.Id) && p.DeletedAt == null)
            .Select(p => new { p.Id, p.Title }).ToDictionaryAsync(p => p.Id, ct);

        var sent = 0;
        foreach (var r in rows)
        {
            if (!users.TryGetValue(r.AssigneeId!.Value, out var user) || !pages.TryGetValue(r.PageId, out var page)) continue;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, ResolveTimeZone(user.TimeZoneId)));
            var daysLeft = r.Due.DayNumber - today.DayNumber;
            // "Tomorrow" goes once on the day before; "today" goes once on the due day. An item saved late (on the due day)
            // only gets "today". Overdue items get nothing: the page already shows them as late.
            var tomorrow = daysLeft == 1 && r.DayBeforeSentAt == null;
            var dueToday = daysLeft == 0 && r.DaySentAt == null;
            if (!tomorrow && !dueToday) continue;
            if (await notes.GetAccessAsync(user.Id, r.PageId, ct) == NoteAccess.None) continue;

            try
            {
                var title = string.IsNullOrWhiteSpace(page.Title) ? "Untitled" : page.Title;
                await gateway.PublishInternalAsync(clientSlug: "notes", eventType: "note.todo_due",
                    title: dueToday ? "To-do due today" : "To-do due tomorrow",
                    message: $"“{r.Text}” in {title}", target: new NotificationTarget(UserId: user.Id), priority: "normal",
                    targetUrl: $"/notes/{r.PageId}", ct: ct);
                // Marked only after the publish worked: a failure is retried on the next pass instead of lost.
                if (tomorrow) r.DayBeforeSentAt = nowUtc;
                else { r.DaySentAt = nowUtc; r.DayBeforeSentAt ??= nowUtc; }   // never send "tomorrow" after "today"
                await db.SaveChangesAsync(ct);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Notes reminder for {Page}/{Item} failed; retried next pass", r.PageId, r.ItemKey);
            }
        }
        return sent;
    }
}
