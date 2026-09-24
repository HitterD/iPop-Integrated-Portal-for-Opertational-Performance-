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
            await db.SaveChangesAsync(ct);

            if (!string.IsNullOrEmpty(op.User?.Nik))
            {
                _ = realtime.ToUserAsync(op.User.Nik, RealtimeEvents.FileOperationProgress,
                    new RealtimeArgs(op.Id, completed, op.TotalFiles, totalBytes), ct);
            }
        }

        op.FinishedAt = DateTime.UtcNow;
        op.Status = failed > 0 ? (completed > 0 ? "CompletedWithErrors" : "Failed") : "Completed";
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(op.User?.Nik))
        {
            _ = realtime.ToUserAsync(op.User.Nik, RealtimeEvents.FileOperationFinished,
                new RealtimeArgs(op.Id, op.Status), ct);

            var duration = (op.FinishedAt.Value - op.StartedAt!.Value).TotalSeconds;
            if (notifs != null && (duration > 30 || failed > 0))
            {
                var title = failed > 0 ? "Transfer Selesai dengan Masalah" : "Transfer File Selesai";
                var body = failed > 0
                    ? $"{completed} file berhasil, {failed} gagal ditransfer."
                    : $"{completed} file berhasil ditransfer ke {op.DestServer}.";
                _ = notifs.PublishInternalAsync("files", "files.transfer", title, body,
                    new NotificationTarget(UserId: op.UserId), failed > 0 ? "high" : "normal", "/files", ct);
            }
        }

        return true;
    }
}
