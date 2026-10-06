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

    /// What is wrong right now, as (key, title, message). Pure so it can be tested.
    public static List<(string Key, string Title, string Message)> Problems(BackupStatus? status, DateTime nowUtc,
        IEnumerable<(string Drive, long FreeBytes)> drives, long minFreeBytes, TimeSpan maxBackupAge)
    {
        var list = new List<(string, string, string)>();
        if (status is null)
            list.Add(("backup-missing", "Database backup not found", "No backup status on the NAS. Check the \"iPop DB backup\" scheduled task."));
        else if (!status.Ok)
            list.Add(("backup-failed", "Database backup failed", $"Last backup failed: {status.Error ?? "unknown error"}."));
        else if (status.FinishedAt is { } at && nowUtc - at > maxBackupAge)
            list.Add(("backup-stale", "Database backup is overdue", $"The last good backup is from {at.ToLocalTime():d MMM HH:mm}."));
        foreach (var (drive, free) in drives)
            if (free < minFreeBytes)
                list.Add(($"disk-{drive}", $"Drive {drive} is almost full", $"Only {free / 1_000_000_000.0:0.#} GB free on {drive}."));
        return list;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var statusPath = config["Ops:BackupStatusPath"] ?? @"\\192.168.2.16\sja\SJA SPJ\ICT\backup\ipop-db\backup-status.json";
        var drives = (config["Ops:WatchDrives"] ?? "C").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var minFree = (long)(double.TryParse(config["Ops:MinFreeGb"], out var g) ? g : 20) * 1_000_000_000L;
        try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await CheckAsync(statusPath, drives, minFree, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "Ops health check failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task CheckAsync(string statusPath, string[] drives, long minFree, CancellationToken ct)
    {
        BackupStatus? status = null;
        try
        {
            if (File.Exists(statusPath))
                status = JsonSerializer.Deserialize<BackupStatus>(await File.ReadAllTextAsync(statusPath, ct),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not read backup status at {Path}", statusPath); }

        var free = drives.Select(d => { try { return (d + ":", new DriveInfo(d).AvailableFreeSpace); } catch { return (d + ":", long.MaxValue); } });
        var now = DateTime.UtcNow;
        var problems = Problems(status, now, free, minFree, TimeSpan.FromHours(30));
        if (problems.Count == 0) return;

        using var scope = scopes.CreateScope();
        var notify = scope.ServiceProvider.GetRequiredService<INotificationGatewayService>();
        foreach (var (key, title, message) in problems)
        {
            if (_lastAlert.TryGetValue(key, out var last) && now - last < TimeSpan.FromHours(24)) continue;
            log.LogWarning("Ops alert: {Title} — {Message}", title, message);
            await notify.PublishInternalAsync("system", "system.ops", title, message,
                new NotificationTarget(Role: "Admin"), "high", "/admin", ct);
            _lastAlert[key] = now;
        }
    }
}
