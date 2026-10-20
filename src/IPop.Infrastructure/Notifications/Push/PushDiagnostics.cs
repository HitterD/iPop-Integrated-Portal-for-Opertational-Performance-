using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using S = IPop.Infrastructure.Notifications.Push.PushDispatcher.Status;

namespace IPop.Infrastructure.Notifications.Push;

/// health: ok | retrying | failing | auth-error | unknown (no attempt in the retention window).
public sealed record PushDeviceHealthDto(Guid Id, string Label, string Provider, bool IsCurrent, DateTime CreatedAt,
    DateTime LastSeenAt, DateTime? LastAcceptedAt, DateTime? LastFailureAt, string? LastFailureCode, string Health, int Pending);
/// DeviceOutcome/DeviceReportedAt: native app only, what the phone itself reported (shown, blocked-channel, ...).
public sealed record PushAttemptDto(Guid DeliveryId, Guid JobId, string Kind, Guid DeviceId, string Status, int Attempts,
    DateTime CreatedAt, DateTime? AcceptedAt, string? Code, string? DeviceOutcome = null, DateTime? DeviceReportedAt = null, string? DeviceDetail = null);
public sealed record PushDiagnosticsDto(bool Enabled, IReadOnlyList<PushDeviceHealthDto> Devices, IReadOnlyList<PushAttemptDto> Recent);
public sealed record PushAdminDiagnosticsDto(bool Enabled, int Subscriptions, IReadOnlyDictionary<string, int> ByProvider,
    int PendingJobs, int PendingDeliveries, int? OldestPendingSeconds,
    IReadOnlyDictionary<string, int> Last24h, IReadOnlyDictionary<string, int> FailureCodes);

/// Sanitized delivery health. Never returns endpoints, keys, titles, bodies or other users' data.
/// "accepted" = the push service took the message; it does not prove Windows displayed it.
public static class PushDiagnostics
{
    /// One user's own browsers and their recent attempts.
    public static async Task<PushDiagnosticsDto> ForUserAsync(IPopDbContext db, Guid userId, Guid? current, bool enabled,
        DateTime now, CancellationToken ct)
    {
        var since = now - PushDispatcher.Retention;
        var subs = await db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == userId)
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new { s.Id, s.Label, s.Endpoint, s.CreatedAt, s.LastSeenAt }).ToListAsync(ct);
        var subIds = subs.Select(s => s.Id).ToList();
        var mine = db.PushDeliveries.AsNoTracking().Where(d => d.UserId == userId && d.CreatedAt >= since);
        var recent = await mine.OrderByDescending(d => d.CreatedAt).Take(20)
            .Select(d => new PushAttemptDto(d.Id, d.JobId, d.Kind, d.SubscriptionId, d.Status, d.Attempts, d.CreatedAt, d.AcceptedAt, d.Code,
                d.DeviceOutcome, d.DeviceReportedAt, d.DeviceDetail))
            .ToListAsync(ct);
        var perDevice = await mine.Where(d => subIds.Contains(d.SubscriptionId))
            .GroupBy(d => d.SubscriptionId)
            .Select(gr => new
            {
                Id = gr.Key,
                LastAccepted = gr.Max(d => d.AcceptedAt),
                Pending = gr.Count(d => d.Status == S.Queued || d.Status == S.Retrying || d.Status == S.Sending),
            })
            .ToListAsync(ct);
        var failures = await mine.Where(d => subIds.Contains(d.SubscriptionId)
                && (d.Status == S.Retrying || d.Status == S.Failed || d.Status == S.AuthError || d.Status == S.Rejected))
            .OrderByDescending(d => d.UpdatedAt).Take(200)
            .Select(d => new { d.SubscriptionId, d.UpdatedAt, d.Code, d.Status })
            .ToListAsync(ct);

        var devices = subs.Select(s =>
        {
            var agg = perDevice.FirstOrDefault(x => x.Id == s.Id);
            var fail = failures.FirstOrDefault(x => x.SubscriptionId == s.Id);
            var failedSinceAccept = fail is not null && (agg?.LastAccepted is null || fail.UpdatedAt > agg.LastAccepted);
            var health = !failedSinceAccept ? (agg?.LastAccepted is not null ? "ok" : "unknown")
                : fail!.Status == S.AuthError ? "auth-error"
                : fail.Status == S.Retrying ? "retrying"
                : "failing";
            return new PushDeviceHealthDto(s.Id, s.Label, PushDispatcher.ProviderOf(s.Endpoint), s.Id == current,
                s.CreatedAt, s.LastSeenAt, agg?.LastAccepted, failedSinceAccept ? fail!.UpdatedAt : null,
                failedSinceAccept ? fail!.Code : null, health, agg?.Pending ?? 0);
        }).ToList();
        return new PushDiagnosticsDto(enabled, devices, recent);
    }

    /// Fleet-wide counts for admins — no users, devices, names or content.
    public static async Task<PushAdminDiagnosticsDto> AggregateAsync(IPopDbContext db, bool enabled, DateTime now, CancellationToken ct)
    {
        var dayAgo = now.AddDays(-1);
        var endpoints = await db.PushSubscriptions.AsNoTracking().Select(s => s.Endpoint).ToListAsync(ct);
        var byProvider = endpoints.GroupBy(PushDispatcher.ProviderOf).ToDictionary(x => x.Key, x => x.Count());
        var pendingQ = db.PushDeliveries.AsNoTracking().Where(d => d.Status == S.Queued || d.Status == S.Retrying || d.Status == S.Sending);
        var pending = await pendingQ.CountAsync(ct);
        var oldest = await pendingQ.OrderBy(d => d.CreatedAt).Select(d => (DateTime?)d.CreatedAt).FirstOrDefaultAsync(ct);
        var unexpanded = await db.PushJobs.AsNoTracking().CountAsync(j => j.ExpandedAt == null, ct);
        var last24 = await db.PushDeliveries.AsNoTracking().Where(d => d.CreatedAt >= dayAgo)
            .GroupBy(d => d.Status).Select(x => new { x.Key, N = x.Count() }).ToListAsync(ct);
        var codes = await db.PushDeliveries.AsNoTracking().Where(d => d.CreatedAt >= dayAgo && d.Code != null)
            .GroupBy(d => d.Code!).Select(x => new { x.Key, N = x.Count() }).ToListAsync(ct);
        return new PushAdminDiagnosticsDto(enabled, endpoints.Count, byProvider, unexpanded, pending,
            oldest is { } t ? (int)(now - t).TotalSeconds : null,
            last24.ToDictionary(x => x.Key, x => x.N), codes.ToDictionary(x => x.Key, x => x.N));
    }
}
