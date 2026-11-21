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
                    var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
                    var gateway = scope.ServiceProvider.GetService<INotificationGatewayService>();
                    if (gateway is null) continue;
                    var sent = await NotesTrashWorker.RunWithAdvisoryLockAsync(db, "ipop-notes-reminder-worker",
                        () => RunOnceAsync(db, new NoteService(db), gateway, DateTime.UtcNow, log, stoppingToken), 0, stoppingToken);
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
        // Coarse window first (UTC date -2..+3 days covers every time zone); paginated so stale rows cannot starve later days.
        var from = DateOnly.FromDateTime(nowUtc.AddDays(-2));
        var to = DateOnly.FromDateTime(nowUtc.AddDays(3));
        const int batchSize = 500;
        var sent = 0;
        // Only a to-do that was fully notified (due today) leaves the filter, so the next page skips just the rows that stayed.
        var skip = 0;
        for (var pass = 0; pass < 20; pass++)
        {
            var left = 0;
            var rows = await db.NoteTodoDues.AsNoTracking()
                .Where(x => !x.Done && x.AssigneeId != null && x.Due >= from && x.Due <= to
                    && (x.DayBeforeSentAt == null || x.DaySentAt == null)
                    && db.NotePages.Any(p => p.Id == x.PageId && p.DeletedAt == null)
                    && db.Users.Any(u => u.Id == x.AssigneeId && u.IsActive))
                .OrderBy(x => x.Due).ThenBy(x => x.PageId).ThenBy(x => x.ItemKey)
                .Skip(skip).Take(batchSize)
                .Select(x => new { x.PageId, x.ItemKey, AssigneeId = x.AssigneeId!.Value, x.Due, x.Text, x.DayBeforeSentAt, x.DaySentAt })
                .ToListAsync(ct);
            if (rows.Count == 0) break;

            var userIds = rows.Select(r => r.AssigneeId).Distinct().ToList();
            var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id) && u.IsActive)
                .Select(u => new { u.Id, u.TimeZoneId }).ToDictionaryAsync(u => u.Id, ct);
            var pageIds = rows.Select(r => r.PageId).Distinct().ToList();
            var pages = await db.NotePages.AsNoTracking().Where(p => pageIds.Contains(p.Id) && p.DeletedAt == null)
                .Select(p => new { p.Id, p.Title }).ToDictionaryAsync(p => p.Id, ct);

            foreach (var r in rows)
            {
                if (!users.TryGetValue(r.AssigneeId, out var user) || !pages.TryGetValue(r.PageId, out var page)) continue;
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, ResolveTimeZone(user.TimeZoneId)));
                var daysLeft = r.Due.DayNumber - today.DayNumber;
                var tomorrow = daysLeft == 1 && r.DayBeforeSentAt == null;
                var dueToday = daysLeft == 0 && r.DaySentAt == null;
                if (!tomorrow && !dueToday) continue;
                if (await notes.GetAccessAsync(user.Id, r.PageId, ct) == NoteAccess.None) continue;

                // Atomically claim before publishing so multi-pod / concurrent saves never double-notify.
                var beforeVal = r.DayBeforeSentAt ?? nowUtc;
                var claimed = tomorrow
                    ? await db.NoteTodoDues
                        .Where(x => x.PageId == r.PageId && x.ItemKey == r.ItemKey && !x.Done && x.DayBeforeSentAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.DayBeforeSentAt, nowUtc), ct)
                    : await db.NoteTodoDues
                        .Where(x => x.PageId == r.PageId && x.ItemKey == r.ItemKey && !x.Done && x.DaySentAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.DaySentAt, nowUtc).SetProperty(x => x.DayBeforeSentAt, beforeVal), ct);
                if (claimed != 1) { left++; continue; }

                try
                {
                    var title = string.IsNullOrWhiteSpace(page.Title) ? "Untitled" : page.Title;
                    await gateway.PublishInternalAsync(clientSlug: "notes", eventType: "note.todo_due",
                        title: dueToday ? "To-do due today" : "To-do due tomorrow",
                        message: $"“{r.Text}” in {title}", target: new NotificationTarget(UserId: user.Id), priority: "normal",
                        targetUrl: $"/notes/{r.PageId}", ct: ct);
                    sent++;
                    if (dueToday) left++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (tomorrow)
                        await db.NoteTodoDues.Where(x => x.PageId == r.PageId && x.ItemKey == r.ItemKey && x.DayBeforeSentAt == nowUtc)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DayBeforeSentAt, (DateTime?)null), CancellationToken.None);
                    else
                        await db.NoteTodoDues.Where(x => x.PageId == r.PageId && x.ItemKey == r.ItemKey && x.DaySentAt == nowUtc)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DaySentAt, (DateTime?)null)
                                .SetProperty(x => x.DayBeforeSentAt, r.DayBeforeSentAt), CancellationToken.None);
                    log.LogWarning(ex, "Notes reminder for {Page}/{Item} failed; retried next pass", r.PageId, r.ItemKey);
                }
            }
            if (rows.Count < batchSize) break;
            skip += rows.Count - left;
        }
        return sent;
    }
}
