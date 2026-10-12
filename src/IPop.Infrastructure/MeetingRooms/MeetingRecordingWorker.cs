using IPop.Application.Files;
using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
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
/// Once a night (02:30 local): recordings past the retention days leave the NAS, and so do deleted ones whose immediate
/// NAS delete failed (they keep their NasPath).
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
        var rooms = scope.ServiceProvider.GetRequiredService<ILiveKitRoomAdmin>();
        var tracker = scope.ServiceProvider.GetService<RoomOccupancyTracker>() ?? new RoomOccupancyTracker();
        var nas = scope.ServiceProvider.GetRequiredService<IFileAccessService>();
        var notify = scope.ServiceProvider.GetRequiredService<INotificationGatewayService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var spool = config["MeetingRooms:SpoolDir"] ?? @"F:\ipop-data\recordings-spool";
        var nasRoot = (config["MeetingRooms:NasFolder"] ?? "/sja/SJA SPJ/ICT/recordings").TrimEnd('/');
        var done = 0;
        var now = DateTime.UtcNow;

        var open = await db.MeetingRecordings.Include(r => r.Viewers)
            .Where(r => r.Status == MeetingRecordingStatus.Recording || r.Status == MeetingRecordingStatus.Processing)
            .ToListAsync(ct);
        foreach (var row in open)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Egress finished but the NAS never took the file: stop retrying and tell whoever started it.
                if (row.Status == MeetingRecordingStatus.Processing && row.EndedAt is { } ended && ended < now - UploadTimeout)
                {
                    row.Status = MeetingRecordingStatus.Failed; row.Error = "upload timed out";
                    await db.SaveChangesAsync(ct);
                    DeleteSpool(spool, row);
                    await NotifyFailedAsync(notify, row, ct);
                    done++;
                    continue;
                }
                if (string.IsNullOrEmpty(row.EgressId))
                {
                    // Start was interrupted between the row insert and storing the Egress id (request aborted,
                    // API restarted): nothing is recording, but the row holds the room's single recording slot.
                    if (row.Status == MeetingRecordingStatus.Recording && row.StartedAt < now - StartGrace)
                    {
                        row.Status = MeetingRecordingStatus.Failed; row.Error = "start interrupted"; row.EndedAt ??= now;
                        await db.SaveChangesAsync(ct);
                        DeleteSpool(spool, row);
                        await ClearBannerAsync(db, rooms, tracker, row, ct);
                        done++;
                    }
                    continue;
                }
                if (row.Status == MeetingRecordingStatus.Recording)
                {
                    var participants = await rooms.ListAsync(MeetingRoomKeys.LiveKitRoom(row.RoomKey), ct);
                    var viewers = row.Viewers.Select(v => v.UserId).ToHashSet();
                    foreach (var participant in participants)
                    {
                        if (participant.Identity.StartsWith("EG_", StringComparison.Ordinal)
                            || !Guid.TryParse(participant.Identity, out var userId) || !viewers.Add(userId)) continue;
                        row.Viewers.Add(new MeetingRecordingViewer { RecordingId = row.Id, UserId = userId });
                    }
                    await db.SaveChangesAsync(ct);
                }
                var info = await egress.GetAsync(row.EgressId, ct);
                if (info is null) continue;
                // Egress ended on its own (abort, failure, limit) while the room still shows "Recording · started by …":
                // clear the banner, or everyone keeps seeing it and Stop answers 404.
                if (row.Status == MeetingRecordingStatus.Recording && info.Status is "EGRESS_FAILED" or "EGRESS_ABORTED" or "EGRESS_COMPLETE" or "EGRESS_LIMIT_REACHED")
                    await ClearBannerAsync(db, rooms, tracker, row, ct);
                if (info.Status is "EGRESS_FAILED" or "EGRESS_ABORTED")
                {
                    row.Status = MeetingRecordingStatus.Failed; row.Error = info.Error; row.EndedAt ??= now;
                    await db.SaveChangesAsync(ct);
                    DeleteSpool(spool, row);
                    await NotifyFailedAsync(notify, row, ct);
                    done++;
                    continue;
                }
                if (info.Status is not ("EGRESS_COMPLETE" or "EGRESS_LIMIT_REACHED")) continue;
                // Egress is done: leave "Recording" now, before the upload, so the room's single recording slot
                // (unique filtered index) frees up even while the NAS is slow or offline.
                if (row.Status == MeetingRecordingStatus.Recording)
                {
                    row.Status = MeetingRecordingStatus.Processing; row.EndedAt ??= now;
                    await db.SaveChangesAsync(ct);
                }

                if (!nas.HasServiceAccount || string.IsNullOrEmpty(row.SpoolPath)) continue;
                var local = Path.Combine(spool, row.SpoolPath!.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(local)) continue;   // the container may still be flushing; try next pass
                var dir = $"{nasRoot}/{Path.GetDirectoryName(row.SpoolPath)!.Replace('\\', '/')}";
                var file = Path.GetFileName(row.SpoolPath);
                await using (var fs = File.OpenRead(local))
                    await nas.UploadAsServiceAsync(dir, file, fs, ct);
                var nasPath = $"{dir}/{file}";
                long? size = info.Size;
                int? duration = info.DurationNs is { } ns ? (int)(ns / 1_000_000_000) : null;
                var endedAt = row.EndedAt ?? now;
                // Conditional: someone may have deleted the recording while it uploaded. A plain save would turn that
                // Deleted row back into Ready.
                var became = await db.MeetingRecordings
                    .Where(r => r.Id == row.Id && r.Status == MeetingRecordingStatus.Processing)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.NasPath, nasPath).SetProperty(r => r.SizeBytes, size)
                        .SetProperty(r => r.DurationSeconds, duration).SetProperty(r => r.EndedAt, endedAt)
                        .SetProperty(r => r.Status, MeetingRecordingStatus.Ready), ct);
                db.Entry(row).State = EntityState.Detached;
                File.Delete(local);
                if (became == 0)
                {
                    // Deleted meanwhile: record the file on the row so the nightly purge removes it if this fails.
                    await db.MeetingRecordings.Where(r => r.Id == row.Id && r.Status == MeetingRecordingStatus.Deleted)
                        .ExecuteUpdateAsync(u => u.SetProperty(r => r.NasPath, nasPath), ct);
                    await nas.DeleteAsServiceAsync(nasPath, ct);
                    await db.MeetingRecordings.Where(r => r.Id == row.Id && r.Status == MeetingRecordingStatus.Deleted)
                        .ExecuteUpdateAsync(u => u.SetProperty(r => r.NasPath, (string?)null), ct);
                    continue;
                }
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
                foreach (var viewer in db.ChangeTracker.Entries<MeetingRecordingViewer>()
                    .Where(e => e.Entity.RecordingId == row.Id && e.State == EntityState.Added).ToList())
                    viewer.State = EntityState.Detached;
            }
        }
        return done;
    }

    /// A Recording row without an Egress id older than this was never started (the start request died midway).
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(2);
    /// A finished recording that has not reached the NAS this long after it ended is given up on.
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromHours(6);

    /// A failed recording's partial file in the spool folder is of no use: remove it (best effort).
    private static void DeleteSpool(string spool, MeetingRecording row)
    {
        if (string.IsNullOrEmpty(row.SpoolPath)) return;
        try
        {
            var local = Path.Combine(spool, row.SpoolPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(local)) File.Delete(local);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    /// Rewrites the room metadata without this recording (the booking info stays). Best effort: the room may be gone.
    private static Task ClearBannerAsync(IPopDbContext db, ILiveKitRoomAdmin rooms, RoomOccupancyTracker tracker, MeetingRecording row,
        CancellationToken ct) =>
        RoomMetadata.PublishAsync(db, rooms, tracker, row.RoomKey, DateTime.UtcNow, ct, endedRecordingId: row.Id);

    private static Task NotifyFailedAsync(INotificationGatewayService notify, MeetingRecording row, CancellationToken ct) =>
        notify.PublishInternalAsync("meetings", "meeting.recording.failed", "Recording failed",
            $"The server recording of {row.Title} could not be saved.", new NotificationTarget(UserId: row.StartedById),
            targetUrl: "/calls?tab=rooms", ct: ct);

    public static async Task<int> PurgeAsync(IServiceProvider sp, DateTime nowUtc, CancellationToken ct, ILogger<MeetingRecordingWorker>? logger = null)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var nas = scope.ServiceProvider.GetRequiredService<IFileAccessService>();
        var days = await scope.ServiceProvider.GetRequiredService<ISettings>().GetAsync(SettingKeys.MeetingRoomsRetentionDays, 90, ct);
        var cutoff = nowUtc.AddDays(-Math.Max(1, days));
        // Failed recordings that never reached the NAS have no file to remove: they simply leave the lists.
        var stale = await db.MeetingRecordings
            .Where(r => r.NasPath == null && r.Status == MeetingRecordingStatus.Failed && r.StartedAt < cutoff)
            .ToListAsync(ct);
        foreach (var r in stale) r.Status = MeetingRecordingStatus.Deleted;
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
        foreach (var r in stale) await MeetingRecordingService.DeleteChatAsync(db, r.Id, ct);
        // Chat of any recording already Deleted (e.g. its delete-time cleanup did not finish) goes too.
        await db.MeetingChatMessages.Where(m => m.RecordingId != null
            && db.MeetingRecordings.Any(r => r.Id == m.RecordingId && r.Status == MeetingRecordingStatus.Deleted)).ExecuteDeleteAsync(ct);
        var deleted = stale.Count;
        if (!nas.HasServiceAccount) return deleted;
        var rows = await db.MeetingRecordings
            .Where(r => r.NasPath != null && (r.Status == MeetingRecordingStatus.Deleted
                || ((r.Status == MeetingRecordingStatus.Ready || r.Status == MeetingRecordingStatus.Failed) && r.StartedAt < cutoff)))
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await nas.DeleteAsServiceAsync(r.NasPath!, ct);
                r.NasPath = null;
                r.Status = MeetingRecordingStatus.Deleted;
                await db.SaveChangesAsync(ct);
                await MeetingRecordingService.DeleteChatAsync(db, r.Id, ct);
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
