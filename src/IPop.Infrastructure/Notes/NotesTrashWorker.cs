using IPop.Application.Attachments;
using IPop.Application.Notes;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notes;

/// <summary>
/// Empties Notes Trash by itself: a page that has been in Trash for 30 days is deleted for good (with its sub-pages,
/// versions and comments), and so are the files that were attached ONLY to those pages. A file that another page still
/// refers to (an image pasted into a second page, a duplicated page) stays. Runs a little after start-up and then every
/// 6 hours, in small batches so it never holds the database for long; a storage failure for one file just leaves that
/// file for the next pass.
/// </summary>
public sealed class NotesTrashWorker(IServiceScopeFactory scopes, ILogger<NotesTrashWorker> log) : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const int PagesPerPass = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
                    var (pages, files) = await RunWithAdvisoryLockAsync(db, "ipop-notes-trash-worker",
                        () => RunOnceAsync(scope.ServiceProvider, Retention, PagesPerPass, log, stoppingToken), (0, 0), stoppingToken);
                    if (pages + files > 0) log.LogInformation("Notes trash: purged {Pages} page(s) and {Files} file(s) older than {Days} days", pages, files, Retention.TotalDays);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogError(ex, "Notes trash clean-up failed; will retry at the next interval"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal static async Task<T> RunWithAdvisoryLockAsync<T>(IPopDbContext db, string lockKey, Func<Task<T>> tick, T fallback, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return await tick();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var got = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock(hashtext({lockKey})) AS \"Value\"").SingleAsync(ct);
            if (!got) return fallback;
            try { return await tick(); }
            finally { await db.Database.SqlQuery<bool>($"SELECT pg_advisory_unlock(hashtext({lockKey})) AS \"Value\"").SingleAsync(CancellationToken.None); }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    /// One pass. Returns (pages purged, files removed from storage).
    public static async Task<(int Pages, int Files)> RunOnceAsync(IServiceProvider sp, TimeSpan retention, int maxPages, ILogger log, CancellationToken ct)
    {
        var notes = sp.GetRequiredService<INoteService>();
        var db = sp.GetRequiredService<IPopDbContext>();
        var before = await db.NotePages.CountAsync(p => p.DeletedAt != null, ct);
        var attachmentIds = await notes.PurgeExpiredTrashAsync(retention, maxPages, ct);
        var after = await db.NotePages.CountAsync(p => p.DeletedAt != null, ct);

        // The pages are gone. A file goes only when no remaining page links it (a file may be linked to several pages).
        var store = sp.GetService<IAttachmentStore>();
        var removed = await DeleteOrphanAttachmentsAsync(db, store, attachmentIds, log, ct);
        return (Math.Max(0, before - after), removed);
    }

    /// Safely deletes unreferenced Notes attachments from storage (or soft-deletes if another live row shares StoragePath).
    public static async Task<int> DeleteOrphanAttachmentsAsync(IPopDbContext db, IAttachmentStore? store, IEnumerable<Guid> attachmentIds,
        ILogger log, CancellationToken ct)
    {
        var removed = 0;
        foreach (var id in attachmentIds.Distinct())
        {
            if (await db.NoteAttachments.AsNoTracking().AnyAsync(a => a.AttachmentId == id, ct)) continue;
            // Another attachment row can point at the very same stored file (Duplicate copies rows, not bytes).
            var row = await db.Attachments.AsNoTracking().Where(a => a.Id == id && a.DeletedAt == null)
                .Select(a => new { a.StoragePath }).FirstOrDefaultAsync(ct);
            if (row is null) continue;
            var sharedBytes = await db.Attachments.AsNoTracking()
                .AnyAsync(a => a.Id != id && a.StoragePath == row.StoragePath && a.DeletedAt == null, ct);
            try
            {
                if (sharedBytes)
                    await db.Attachments.Where(a => a.Id == id && a.DeletedAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, DateTime.UtcNow), ct);   // keep the bytes
                else if (store is not null)
                    await store.DeleteAsync(id, ct);
                removed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Notes trash: couldn't remove file {Id}; the next pass retries it", id);
            }
        }
        return removed;
    }
}
