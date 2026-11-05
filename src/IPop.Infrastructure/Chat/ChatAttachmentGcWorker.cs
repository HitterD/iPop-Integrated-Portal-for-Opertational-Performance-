using IPop.Application.Files;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Chat;

/// <summary>
/// Hourly clean-up of chat files nobody can reach any more, rows AND the files behind them:
/// uploads never sent (MessageId null) after 24 h, and files of deleted messages after 30 days (until then they are
/// hidden from members but still on the NAS, in case a deletion has to be undone or audited).
/// Files on the NAS go through the service account; local files (dev / pre-NAS) are deleted from disk. A row is only
/// removed once its file is gone (or was never there), so a NAS outage just postpones the clean-up.
/// </summary>
public sealed class ChatAttachmentGcWorker(IServiceScopeFactory scopes, ILogger<ChatAttachmentGcWorker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan DeletedMessageRetention = TimeSpan.FromDays(30);
    private const int Batch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var removed = await RunOnceAsync(scope.ServiceProvider.GetRequiredService<IPopDbContext>(),
                        scope.ServiceProvider.GetService<IFileAccessService>(),
                        scope.ServiceProvider.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.ContentRootPath,
                        DateTime.UtcNow, log, stoppingToken);
                    if (removed > 0) log.LogInformation("ChatAttachment GC: removed {Count} unreachable attachments", removed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { log.LogError(ex, "ChatAttachment GC failed; will retry next interval"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// One pass; returns how many attachment rows were removed.
    public static async Task<int> RunOnceAsync(IPopDbContext db, IFileAccessService? files, string? contentRoot, DateTime now,
        ILogger log, CancellationToken ct)
    {
        var orphanCutoff = now - OrphanAge;
        var deletedCutoff = now - DeletedMessageRetention;
        var due = await db.ChatAttachments
            .Where(a => (a.MessageId == null && a.CreatedAt < orphanCutoff)
                || (a.MessageId != null && a.Message.IsDeleted && a.Message.DeletedAt != null && a.Message.DeletedAt < deletedCutoff))
            .OrderBy(a => a.CreatedAt)
            .Take(Batch)
            .ToListAsync(ct);
        var removed = 0;
        foreach (var a in due)
        {
            // Forwarding copies attachment rows, not files: both original and thumbnail paths can still be in use.
            // Count every other reference, including pending uploads and deleted messages still within retention.
            var paths = new[] { a.SynologyPath, a.ThumbnailPath }
                .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
            var goneOrShared = true;
            foreach (var path in paths)
            {
                var shared = await db.ChatAttachments.AsNoTracking().AnyAsync(other => other.Id != a.Id
                    && other.SynologyServer == a.SynologyServer
                    && (other.SynologyPath == path || other.ThumbnailPath == path), ct);
                if (shared) continue;
                if (!await DeleteFileAsync(a.SynologyServer, path!, files, contentRoot, log, ct))
                {
                    goneOrShared = false;
                    break;
                }
            }
            if (!goneOrShared) continue;
            db.ChatAttachments.Remove(a);
            // Persist each removal so the last expired reference in this pass can actually collect the file.
            await db.SaveChangesAsync(ct);
            removed++;
        }
        return removed;
    }

    /// True when the file is gone afterwards (deleted now, or already missing).
    private static async Task<bool> DeleteFileAsync(string server, string path, IFileAccessService? files, string? contentRoot,
        ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (server == "local")
        {
            if (contentRoot is null) return false;
            var full = Path.GetFullPath(Path.Combine(contentRoot, path));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot)) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(root, comparison)) return true; // a sibling with the same prefix is outside the app
            try { if (File.Exists(full)) File.Delete(full); return true; }
            catch (IOException e) { log.LogWarning("ChatAttachment GC: could not delete {Path} ({Error})", path, e.Message); return false; }
            catch (UnauthorizedAccessException e) { log.LogWarning("ChatAttachment GC: could not delete {Path} ({Error})", path, e.Message); return false; }
        }
        if (files is null || !files.HasServiceAccount) return false;
        try { await files.DeleteAsServiceAsync(path, ct); return true; }
        // FileStation 408 = "no such file or directory": already gone.
        catch (SynoException e) when (e.Code == 408) { return true; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("ChatAttachment GC: NAS delete of {Path} failed ({Error}); retried next hour", path, e.Message);
            return false;
        }
    }
}
