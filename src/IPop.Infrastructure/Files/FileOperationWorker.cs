using System.Text.Json;
using IPop.Application.Files;
using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Files;

public record FileTransferItem(string Path, string Name, long Size);

public class FileOperationWorker(
    IServiceScopeFactory scopes,
    ILogger<FileOperationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RecoverInterruptedAsync(stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Recovering interrupted file operations failed.");
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNextOperationAsync(stoppingToken);
                if (!processed)
                {
                    await Task.Delay(Interval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in FileOperationWorker execution.");
                await Task.Delay(Interval, stoppingToken);
            }
        }
    }

    /// One API replica runs this worker, so anything still "Running" at startup was left by a process that died
    /// (pod killed, node lost, DB failover mid-save). Such operations used to spin forever on the user's dashboard.
    /// They are failed, not resumed: a half-finished move may already have deleted some sources.
    public async Task<int> RecoverInterruptedAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var notifs = scope.ServiceProvider.GetService<INotificationGatewayService>();
        var orphans = await db.FileOperations.Where(o => o.Status == "Running").ToListAsync(ct);
        if (orphans.Count == 0) return 0;
        var now = DateTime.UtcNow;
        foreach (var o in orphans)
        {
            o.Status = "Failed";
            o.FinishedAt = now;
            o.Error = $"Transfer interrupted by a server restart after {o.CompletedFiles} of {o.TotalFiles} file(s). Check the destination and start it again.";
        }
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogWarning("Marked {Count} interrupted file operation(s) as failed.", orphans.Count);
        if (notifs is not null)
            foreach (var o in orphans)
            {
                try
                {
                    await notifs.PublishInternalAsync("files", "files.transfer", "Transfer interrupted",
                        $"{o.CompletedFiles} of {o.TotalFiles} file(s) were transferred before the server restarted.",
                        new NotificationTarget(UserId: o.UserId), "high", "/files", CancellationToken.None);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Notifying about interrupted op {Id} failed.", o.Id); }
            }
        return orphans.Count;
    }

    public async Task<bool> ProcessNextOperationAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var fileSvc = scope.ServiceProvider.GetRequiredService<IFileAccessService>();
        var realtime = scope.ServiceProvider.GetRequiredService<IRealtime>();
        var notifs = scope.ServiceProvider.GetService<INotificationGatewayService>();

        // Claim next queued operation
        var op = await db.FileOperations.Include(o => o.User)
            .Where(o => o.Status == "Queued")
            .OrderBy(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (op is null) return false;

        op.Status = "Running";
        op.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        List<FileTransferItem> items = [];
        try
        {
            items = JsonSerializer.Deserialize<List<FileTransferItem>>(op.ItemsJson) ?? [];
        }
        catch (Exception ex)
        {
            op.Status = "Failed";
            op.Error = $"Invalid items payload: {ex.Message}";
            op.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return true;
        }

        var completed = 0;
        var failed = 0;
        long totalBytes = 0;
        var destDir = string.IsNullOrWhiteSpace(op.DestDir) ? "/" : op.DestDir.Trim();

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                if (op.SourceServer.Equals(op.DestServer, StringComparison.OrdinalIgnoreCase))
                {
                    // Same server copy or move
                    var removeSrc = op.Kind.Equals("move", StringComparison.OrdinalIgnoreCase);
                    await fileSvc.CopyMoveAsync(op.UserId, op.SourceServer, [item.Path], destDir, removeSrc, true, ct);
                }
                else
                {
                    // Cross-server stream transfer
                    await using var srcStream = await fileSvc.DownloadAsync(op.UserId, op.SourceServer, item.Path, ct);

                    // If file > 8MB, buffer into temp file
                    if (item.Size > 8 * 1024 * 1024)
                    {
                        var tempPath = Path.Combine(Path.GetTempPath(), $"ipop-transfer-{Guid.NewGuid():N}.tmp");
                        try
                        {
                            await using (var fs = File.Create(tempPath))
                            {
                                await srcStream.CopyToAsync(fs, ct);
                            }

                            await using (var fs = File.OpenRead(tempPath))
                            {
                                await fileSvc.UploadAsync(op.UserId, op.DestServer, destDir, item.Name, fs, ct);
                            }
                        }
                        finally
                        {
                            if (File.Exists(tempPath)) File.Delete(tempPath);
                        }
                    }
                    else
                    {
                        using var ms = new MemoryStream();
                        await srcStream.CopyToAsync(ms, ct);
                        ms.Position = 0;
                        await fileSvc.UploadAsync(op.UserId, op.DestServer, destDir, item.Name, ms, ct);
                    }

                    if (op.Kind.Equals("move", StringComparison.OrdinalIgnoreCase))
                    {
                        try { await fileSvc.DeleteAsync(op.UserId, op.SourceServer, item.Path, ct); }
                        catch { /* non-fatal source removal failure */ }
                    }
                }

                completed++;
                totalBytes += item.Size;
            }
            catch (Exception ex)
            {
                failed++;
                op.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                logger.LogWarning(ex, "Failed to transfer file {Path} in op {Id}", item.Path, op.Id);
            }

            op.CompletedFiles = completed;
            op.FailedFiles = failed;
            op.BytesTransferred = totalBytes;
            await db.SaveChangesAsync(CancellationToken.None);

            if (!string.IsNullOrEmpty(op.User?.Nik))
            {
                _ = realtime.ToUserAsync(op.User.Nik, RealtimeEvents.FileOperationProgress,
                    new RealtimeArgs(op.Id, completed, op.TotalFiles, totalBytes), ct);
            }
        }

        op.FinishedAt = DateTime.UtcNow;
        op.Status = failed > 0 ? (completed > 0 ? "CompletedWithErrors" : "Failed") : "Completed";
        if (ct.IsCancellationRequested && completed + failed < items.Count)
        {
            // Shutdown stopped the loop early: record it honestly instead of "Completed" with files missing.
            op.Status = "Failed";
            op.Error = $"Transfer interrupted by a server restart after {completed} of {items.Count} file(s). Check the destination and start it again.";
        }
        // Not cancellable: a cancelled save here used to leave the operation "Running" forever.
        await db.SaveChangesAsync(CancellationToken.None);

        // Every open view of the destination (and of the source folders, for a move) reloads.
        if (completed > 0)
        {
            await FilesRealtime.PushAsync(realtime, op.DestServer, [destDir]);
            if (op.Kind.Equals("move", StringComparison.OrdinalIgnoreCase))
                await FilesRealtime.PushAsync(realtime, op.SourceServer, items.Select(i => FilesRealtime.ParentDir(i.Path)));
        }

        if (!string.IsNullOrEmpty(op.User?.Nik))
        {
            _ = realtime.ToUserAsync(op.User.Nik, RealtimeEvents.FileOperationFinished,
                new RealtimeArgs(op.Id, op.Status), ct);

            var duration = (op.FinishedAt.Value - op.StartedAt!.Value).TotalSeconds;
            if (notifs != null && (duration > 30 || failed > 0))
            {
                var title = failed > 0 ? "Transfer Finished with Errors" : "File Transfer Complete";
                var body = failed > 0
                    ? $"{completed} file(s) transferred, {failed} failed."
                    : $"{completed} file(s) transferred to {op.DestServer}.";
                // Awaited: the gateway uses this scope's DbContext, which is disposed as soon as
                // we return — a fire-and-forget publish raced (or outlived) it and was lost.
                try
                {
                    await notifs.PublishInternalAsync("files", "files.transfer", title, body,
                        new NotificationTarget(UserId: op.UserId), failed > 0 ? "high" : "normal", "/files", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Publishing the transfer notification for op {Id} failed.", op.Id);
                }
            }
        }

        return true;
    }
}
