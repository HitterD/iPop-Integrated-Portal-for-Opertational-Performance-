using IPop.Application.Notes;
using IPop.Application.Notifications;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notes;

/// <summary>
/// Repeating pages ("Repeat…" on a page): from 06:00 Jakarta time on a day the rule falls on, a copy of the page is made next to it, titled
/// "Name — 24 Nov 2026", and the people who can open it are told. At most one page per rule per local day (LastCreatedFor), claimed with a
/// compare-and-set before the page is made, and one pod at a time (advisory lock), so a rolling deploy or a retry never makes two. A page that
/// was not made (the template was trashed, or its owner lost access) is skipped and tried again on the next tick the same day.
/// </summary>
public sealed class NotesRecurrenceWorker(IServiceScopeFactory scopes, ILogger<NotesRecurrenceWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
                    var gateway = scope.ServiceProvider.GetService<INotificationGatewayService>();
                    var made = await RunExclusiveAsync(db, () => RunOnceAsync(db, new NoteService(db), gateway, DateTime.UtcNow, log, stoppingToken), stoppingToken);
                    if (made > 0) log.LogInformation("Notes recurrence: made {Count} page(s)", made);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogWarning(ex, "Notes recurrence pass failed; will retry"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static async Task<int> RunExclusiveAsync(IPopDbContext db, Func<Task<int>> tick, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return await tick();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var got = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock(hashtext('ipop-notes-recurrence-worker')) AS \"Value\"").SingleAsync(ct);
            if (!got) return 0;
            try { return await tick(); }
            finally { await db.Database.SqlQuery<bool>($"SELECT pg_advisory_unlock(hashtext('ipop-notes-recurrence-worker')) AS \"Value\"").SingleAsync(CancellationToken.None); }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    /// <summary>One pass; returns how many pages were made.</summary>
    public static async Task<int> RunOnceAsync(IPopDbContext db, INoteService notes, INotificationGatewayService? gateway, DateTime utcNow, ILogger log, CancellationToken ct)
    {
        var local = NoteRecurrenceRules.LocalNow(utcNow);
        if (local.Hour < NoteRecurrenceRules.CreateFromHour) return 0;
        var today = DateOnly.FromDateTime(local);

        var rules = await db.NoteRecurrences.AsNoTracking().Where(r => r.Enabled && r.StartsOn <= today && (r.LastCreatedFor == null || r.LastCreatedFor < today))
            .OrderBy(r => r.CreatedAt).Take(200).ToListAsync(ct);
        var made = 0;
        foreach (var r in rules)
        {
            if (!NoteRecurrenceRules.IsDueOn(r.Rule, r.StartsOn, r.LastCreatedFor, today)) continue;

            // Claim today before making anything: a second pod (or a retry) that gets here finds it already claimed and does nothing.
            var claimed = await db.NoteRecurrences.Where(x => x.Id == r.Id && x.Enabled && (x.LastCreatedFor == null || x.LastCreatedFor < today))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastCreatedFor, today), ct);
            if (claimed != 1) continue;

            try
            {
                var templateTitle = await db.NotePages.AsNoTracking().Where(p => p.Id == r.PageId).Select(p => p.Title).FirstOrDefaultAsync(ct);
                var run = await notes.CreateRecurringCopyAsync(r.PageId, r.OwnerId, NoteRecurrenceRules.TitleFor(templateTitle, today), ct);
                if (!run.Success)
                {
                    // Not made (template trashed, owner lost access): release today so a later tick can retry; a trashed template stays quiet.
                    await db.NoteRecurrences.Where(x => x.Id == r.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastCreatedFor, r.LastCreatedFor), ct);
                    if (run.Error is "forbidden" or "not_found")
                        await db.NoteRecurrences.Where(x => x.Id == r.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false), ct);
                    log.LogInformation("Repeat {Rule} for page {Page} not made: {Error}", r.Id, r.PageId, run.Error);
                    continue;
                }
                await db.NoteRecurrences.Where(x => x.Id == r.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastPageId, run.Data!.PageId), ct);
                made++;
                if (gateway is not null)
                    foreach (var uid in run.Data!.NotifyUserIds.Take(200))
                    {
                        try
                        {
                            await gateway.PublishInternalAsync("notes", "note.recurring_page", "New page", $"“{run.Data.Title}” was created",
                                new NotificationTarget(UserId: uid), "normal", $"/notes/{run.Data.PageId}", ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Repeat notice to {User} failed", uid); }
                    }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the page may or may not exist: keep the claim (never a second page for the same day) and say so
                log.LogWarning(ex, "Repeat {Rule} for page {Page} failed after it was claimed", r.Id, r.PageId);
            }
        }
        return made;
    }
}
