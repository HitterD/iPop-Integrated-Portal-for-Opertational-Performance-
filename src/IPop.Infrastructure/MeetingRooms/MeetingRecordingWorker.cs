using IPop.Application.Files;
using IPop.Application.Hall;
using IPop.Application.Notifications;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.MeetingRooms;

/// Every 15 s: finished Egress recordings go from the spool folder to the NAS and their viewers are told.
/// Once a night (02:30 local): recordings past the retention days, and ones an admin deleted, leave the NAS.
public sealed class MeetingRecordingWorker(IServiceProvider services, ILogger<MeetingRecordingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPurge = DateOnly.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await FinishAsync(services, stoppingToken, log); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogWarning(e, "Meeting recording finish pass failed"); }

            // A finish failure must not prevent the independent nightly retention pass.
            var local = DateTime.Now;
            if (local.TimeOfDay >= new TimeSpan(2, 30, 0) && DateOnly.FromDateTime(local) != lastPurge)
            {
                try
                {
                    await PurgeAsync(services, DateTime.UtcNow, stoppingToken, log);
                    lastPurge = DateOnly.FromDateTime(local);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception e) { log.LogWarning(e, "Meeting recording purge pass failed"); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public static async Task<int> FinishAsync(IServiceProvider sp, CancellationToken ct, ILogger<MeetingRecordingWorker>? logger = null)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var egress = scope.ServiceProvider.GetRequiredService<ILiveKitEgress>();
        var nas = scope.ServiceProvider.GetRequiredService<IFileAccessService>();
        var notify = scope.ServiceProvider.GetRequiredService<INotificationGatewayService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var spool = config["MeetingRooms:SpoolDir"] ?? @"F:\ipop-data\recordings-spool";
        var nasRoot = (config["MeetingRooms:NasFolder"] ?? "/sja/SJA SPJ/ICT/recordings").TrimEnd('/');
        var done = 0;

        var open = await db.MeetingRecordings.Include(r => r.Viewers)
            .Where(r => r.Status == MeetingRecordingStatus.Recording || r.Status == MeetingRecordingStatus.Processing)
            .ToListAsync(ct);
        foreach (var row in open)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrEmpty(row.EgressId)) continue;
                var info = await egress.GetAsync(row.EgressId, ct);
                if (info is null) continue;
                if (info.Status is "EGRESS_FAILED" or "EGRESS_ABORTED")
                {
                    row.Status = MeetingRecordingStatus.Failed; row.Error = info.Error; row.EndedAt ??= DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await notify.PublishInternalAsync("meetings", "meeting.recording.failed", "Recording failed",
                        $"The server recording of {row.Title} could not be saved.", new NotificationTarget(UserId: row.StartedById),
                        targetUrl: "/calls?tab=rooms", ct: ct);
                    done++;
                    continue;
                }
                if (info.Status is not ("EGRESS_COMPLETE" or "EGRESS_LIMIT_REACHED")) continue;

                if (!nas.HasServiceAccount || string.IsNullOrEmpty(row.SpoolPath)) continue;
                var local = Path.Combine(spool, row.SpoolPath!.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(local)) continue;   // the container may still be flushing; try next pass
                var dir = $"{nasRoot}/{Path.GetDirectoryName(row.SpoolPath)!.Replace('\\', '/')}";
                var file = Path.GetFileName(row.SpoolPath);
                await using (var fs = File.OpenRead(local))
                    await nas.UploadAsServiceAsync(dir, file, fs, ct);
                row.NasPath = $"{dir}/{file}";
                row.SizeBytes = info.Size;
                row.DurationSeconds = info.DurationNs is { } ns ? (int)(ns / 1_000_000_000) : null;
                row.Status = MeetingRecordingStatus.Ready;
                row.EndedAt ??= DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                File.Delete(local);
                await notify.PublishInternalAsync("meetings", "meeting.recording.ready", "Recording ready",
                    $"{row.Title} ({HallBooking.Rooms.FirstOrDefault(r => r.Key == row.RoomKey).Name}) can be watched now.",
                    new NotificationTarget(UserIds: row.Viewers.Select(v => v.UserId).ToList()),
                    targetUrl: $"/calls?tab=rooms&recording={row.Id}", ct: ct);
                done++;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger?.LogWarning(e, "Meeting recording {RecordingId} finish failed", row.Id);
                // Do not let unsaved changes from this row leak into another row's save.
                db.Entry(row).State = EntityState.Unchanged;
            }
        }
        return done;
    }

    public static async Task<int> PurgeAsync(IServiceProvider sp, DateTime nowUtc, CancellationToken ct, ILogger<MeetingRecordingWorker>? logger = null)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var nas = scope.ServiceProvider.GetRequiredService<IFileAccessService>();
        if (!nas.HasServiceAccount) return 0;
        var days = await scope.ServiceProvider.GetRequiredService<ISettings>().GetAsync(SettingKeys.MeetingRoomsRetentionDays, 90, ct);
        var cutoff = nowUtc.AddDays(-Math.Max(1, days));
        var rows = await db.MeetingRecordings
            .Where(r => r.NasPath != null && (r.Status == MeetingRecordingStatus.Deleted
                || ((r.Status == MeetingRecordingStatus.Ready || r.Status == MeetingRecordingStatus.Failed) && r.StartedAt < cutoff)))
            .ToListAsync(ct);
        var deleted = 0;
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await nas.DeleteAsServiceAsync(r.NasPath!, ct);
                r.NasPath = null;
                r.Status = MeetingRecordingStatus.Deleted;
                await db.SaveChangesAsync(ct);
                deleted++;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger?.LogWarning(e, "Meeting recording {RecordingId} purge failed", r.Id);
                db.Entry(r).State = EntityState.Unchanged;
            }
        }
        return deleted;
    }
}
