using IPop.Application.Files;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Mail;

/// <summary>
/// Removes mail uploads that never became part of a sent mail (abandoned composer, file removed from a draft) after
/// 7 days - up to 500 MB each, they used to stay on the NAS forever. Forwards copy the attachment row but point at the
/// SAME NAS file, so the file is deleted only when no other row still references that path; otherwise only the row
/// goes. A row is removed only once its file is gone (or still in use elsewhere), so a NAS outage just postpones it.
/// </summary>
public sealed class MailAttachmentGcWorker(IServiceScopeFactory scopes, ILogger<MailAttachmentGcWorker> log) : BackgroundService
{
    public static readonly TimeSpan OrphanAge = TimeSpan.FromDays(7);
    private const int Batch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var n = await RunOnceAsync(scope.ServiceProvider.GetRequiredService<IPopDbContext>(),
                        scope.ServiceProvider.GetService<IFileAccessService>(),
                        scope.ServiceProvider.GetService<IHostEnvironment>()?.ContentRootPath, DateTime.UtcNow, log, stoppingToken);
                    if (n > 0) log.LogInformation("Mail attachment GC: removed {Count} never-sent uploads", n);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogError(ex, "Mail attachment GC failed; will retry"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public static async Task<int> RunOnceAsync(IPopDbContext db, IFileAccessService? files, string? contentRoot, DateTime now,
        ILogger log, CancellationToken ct)
    {
        var cutoff = now - OrphanAge;
        var due = await db.MailAttachments
            .Where(a => a.MailId == null && a.CreatedAt < cutoff)
            .OrderBy(a => a.CreatedAt).Take(Batch).ToListAsync(ct);
        var removed = new List<MailAttachment>();
        foreach (var a in due)
        {
            var sharedPaths = new[] { a.SynologyPath, a.ThumbnailPath }.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            var gone = true;
            foreach (var path in sharedPaths)
            {
                var inUse = await db.MailAttachments.AnyAsync(o => o.Id != a.Id && (o.SynologyPath == path || o.ThumbnailPath == path), ct);
                if (inUse) continue;   // a forward (or another row) still shows this file
                if (!await DeleteAsync(a.SynologyServer, path!, files, contentRoot, log, ct)) { gone = false; break; }
            }
            if (gone) removed.Add(a);
        }
        if (removed.Count == 0) return 0;
        db.MailAttachments.RemoveRange(removed);
        await db.SaveChangesAsync(ct);
        return removed.Count;
    }

    private static async Task<bool> DeleteAsync(string server, string path, IFileAccessService? files, string? contentRoot,
        ILogger log, CancellationToken ct)
    {
        if (server == "local")
        {
            if (contentRoot is null) return false;
            var root = Path.GetFullPath(Path.Combine(contentRoot, "mail-files"));
            var full = Path.GetFullPath(Path.Combine(contentRoot, path));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            try { if (File.Exists(full)) File.Delete(full); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Mail GC: {Path}: {Error}", path, e.Message); return false; }
        }
        // Only files under the shared mail folder are the service account's to delete; legacy per-user uploads stay.
        if (files is null || !files.HasServiceAccount || !string.Equals(server, files.ServiceServer, StringComparison.OrdinalIgnoreCase)) return false;
        try { await files.DeleteAsServiceAsync(path, ct); return true; }
        catch (SynoException e) when (e.Code == 408) { return true; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("Mail GC: NAS delete of {Path} failed ({Error}); retried later", path, e.Message);
            return false;
        }
    }
}
