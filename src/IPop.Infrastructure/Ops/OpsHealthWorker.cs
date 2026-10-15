using System.Text.Json;
using IPop.Application.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Ops;

/// Hourly server health check for admins: the nightly DB backup (backup-status.json written by
/// deploy/backup/backup-ipop-db.ps1 next to the dumps on the NAS) and free space on the data drives.
/// Each problem notifies the Admin role at most once per day, so a broken backup can't go unnoticed.
public sealed class OpsHealthWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<OpsHealthWorker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly Dictionary<string, DateTime> _lastAlert = [];

    public sealed record BackupStatus(bool Ok, string? File, string? Error, DateTime? FinishedAt, long Bytes);

    /// Two levels: below <see cref="WarnPercent"/> free is a normal-priority warning; below <see cref="CriticalPercent"/>
    /// or <see cref="CriticalFloorBytes"/> it is critical (high priority).
    public sealed record DiskLimits(double WarnPercent, double CriticalPercent, long CriticalFloorBytes);

    public sealed record Problem(string Key, string Title, string Message, string Priority);

    /// What is wrong right now. Pure so it can be tested.
    public static List<Problem> Problems(BackupStatus? status, DateTime nowUtc,
        IEnumerable<(string Drive, long FreeBytes, long TotalBytes)> drives, DiskLimits limits, TimeSpan maxBackupAge)
    {
        var list = new List<Problem>();
        if (status is null)
            list.Add(new("backup-missing", "Database backup not found", "No backup status on the NAS. Check the \"iPop DB backup\" scheduled task.", "high"));
        else if (!status.Ok)
            list.Add(new("backup-failed", "Database backup failed", $"Last backup failed: {status.Error ?? "unknown error"}.", "high"));
        else if (status.FinishedAt is { } at && nowUtc - at > maxBackupAge)
            list.Add(new("backup-stale", "Database backup is overdue", $"The last good backup is from {at.ToLocalTime():d MMM HH:mm}.", "high"));
        foreach (var (drive, free, total) in drives)
        {
            if (total <= 0) continue;
            var percent = free * 100.0 / total;
            var detail = $"{free / 1_000_000_000.0:0.#} GB free of {total / 1_000_000_000.0:0.#} GB ({Math.Floor(percent):0}%) on {drive}.";
            if (percent < limits.CriticalPercent || free < limits.CriticalFloorBytes)
                list.Add(new($"disk-{drive}-critical", $"Drive {drive} is almost full", detail, "high"));
            else if (percent < limits.WarnPercent)
                list.Add(new($"disk-{drive}-warning", $"Drive {drive} is getting full", detail, "normal"));
        }
        return list;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var statusPath = config["Ops:BackupStatusPath"] ?? @"\\192.168.2.16\sja\SJA SPJ\ICT\backup\ipop-db\backup-status.json";
        var drives = (config["Ops:WatchDrives"] ?? "C").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var limits = new DiskLimits(
            double.TryParse(config["Ops:DiskWarnPercent"], out var w) ? w : 10,
            double.TryParse(config["Ops:DiskCriticalPercent"], out var c) ? c : 5,
            (long)((double.TryParse(config["Ops:DiskCriticalFloorGb"], out var g) ? g : 10) * 1_000_000_000L));
        try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await CheckAsync(statusPath, drives, limits, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "Ops health check failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task CheckAsync(string statusPath, string[] drives, DiskLimits limits, CancellationToken ct)
    {
        BackupStatus? status = null;
        try
        {
            if (File.Exists(statusPath))
                status = JsonSerializer.Deserialize<BackupStatus>(await File.ReadAllTextAsync(statusPath, ct),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not read backup status at {Path}", statusPath); }

        var free = drives.Select(d =>
        {
            try { var info = new DriveInfo(d); return (d + ":", info.AvailableFreeSpace, info.TotalSize); }
            catch { return (d + ":", 0L, 0L); } // unreadable drive: skipped (total 0)
        });
        var now = DateTime.UtcNow;
        var problems = Problems(status, now, free, limits, TimeSpan.FromHours(30));
        if (problems.Count == 0) return;

        using var scope = scopes.CreateScope();
        var notify = scope.ServiceProvider.GetRequiredService<INotificationGatewayService>();
        foreach (var p in problems)
        {
            if (_lastAlert.TryGetValue(p.Key, out var last) && now - last < TimeSpan.FromHours(24)) continue;
            log.LogWarning("Ops alert: {Title} — {Message}", p.Title, p.Message);
            await notify.PublishInternalAsync("system", "system.ops", p.Title, p.Message,
                new NotificationTarget(Role: "Admin"), p.Priority, "/admin", ct);
            _lastAlert[p.Key] = now;
        }
    }
}
