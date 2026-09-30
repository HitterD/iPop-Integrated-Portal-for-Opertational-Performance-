using IPop.Application.Hris;
using IPop.Application.Notifications;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Hris;

/// Sync upserts Users only. Departments and sites are owned by the admin, not HRIS —
/// the feed's department codes are ignored entirely (they never set DepartmentId).
/// Insert-only for profiles: a NIK already in iPop keeps every field as iPop has it
/// (admins and the user own them from then on). The one thing the feed still drives on an
/// existing row is activation — statusAktif=false → soft deactivate + revoke sessions, so
/// a leaver cannot keep signing in.
/// Every run keeps a step log (HrisSyncRun.Log); a failure alerts admins when
/// hris.notifyOnFailure is on.
public class HrisSyncService(IPopDbContext db, IHrisGateway gateway,
    INotificationGatewayService? notificationGateway = null, ISettings? settings = null) : IHrisSyncService
{
    private readonly List<string> _log = [];

    private void Log(string line) => _log.Add($"{DateTime.UtcNow:HH:mm:ss} {line}");

    public Task<HrisSyncResult> RunAsync(CancellationToken ct) => RunAsync(null, ct);

    public async Task<HrisSyncResult> RunAsync(string? triggeredBy, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        _log.Clear();
        var run = new HrisSyncRun { Id = Guid.NewGuid(), StartedAt = startedAt, TriggeredBy = triggeredBy };
        Log(triggeredBy is null ? "Scheduled run started" : $"Started by {triggeredBy}");
        db.HrisSyncRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var state = await db.HrisSyncState.FirstAsync(s => s.Id == 1, ct);

        try
        {
            return await RunCoreAsync(state, run, startedAt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is stopping
            throw;
        }
        catch (Exception ex)
        {
            run.FinishedAt = DateTime.UtcNow;
            run.Succeeded = false;
            run.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            Log($"Failed: {run.Error}");
            run.Log = string.Join('\n', _log);
            try { await db.SaveChangesAsync(CancellationToken.None); } catch { }
            await AlertFailureAsync(run.Error);
            throw;
        }
    }

    private async Task<HrisSyncResult> RunCoreAsync(HrisSyncState state, HrisSyncRun run,
        DateTime startedAt, CancellationToken ct)
    {
        var now = startedAt;
        Log($"Connecting to HRIS gateway ({(state.LastUpdated is null ? "full pull" : $"changes since {state.LastUpdated:yyyy-MM-dd HH:mm}")})");

        // The feed can carry the same NIK twice — duplicate source rows, or a page boundary
        // shifting under a paginated pull. `users` below is read once, before the insert loop,
        // so a repeated NIK would queue two inserts and break the unique index for the whole
        // run. Collapse to the freshest record per NIK.
        var employees = (await gateway.SyncEmployeesAsync(state.LastUpdated, ct))
            .GroupBy(e => e.Nik)
            .Select(g => g.MaxBy(e => e.UpdatedAt)!)
            .ToList();
        Log($"Fetched {employees.Count} employee records");

        var inserted = 0;
        var updated = 0;
        var deactivated = 0;

        var users = await db.Users.ToDictionaryAsync(u => u.Nik, ct);
        foreach (var e in employees)
        {
            if (users.TryGetValue(e.Nik, out var u))
            {
                var changed = false;

                if (!e.StatusAktif && u.IsActive)
                {
                    u.IsActive = false;
                    changed = true;
                    deactivated++;
                    await db.AuthSessions
                        .Where(s => s.UserId == u.Id && s.RevokedAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
                }
                else if (e.StatusAktif && !u.IsActive)
                {
                    u.IsActive = true;
                    u.UpdatedAt = now;
                    changed = true;
                }
                if (changed) updated++;
            }
            else
            {
                db.Users.Add(new User
                {
                    Id = Guid.NewGuid(),
                    Nik = e.Nik,
                    Name = e.Nama,
                    Email = e.Email,
                    Position = e.Jabatan,
                    ManagerNik = e.NikAtasan,
                    Extension = e.Ekstensi,
                    Location = e.Lokasi,
                    IsActive = e.StatusAktif,
                    PasswordHash = null,
                    MustChangePassword = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                inserted++;
            }
        }
        await db.SaveChangesAsync(ct);

        state.LastUpdated = employees.Count > 0 ? employees.Max(e => e.UpdatedAt) : state.LastUpdated;
        if (state.LastFullSyncAt is null && employees.Count > 0) state.LastFullSyncAt = now;
        else if (employees.Count > 0) state.LastIncrementalAt = now;

        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = null,
            Action = "hris.sync",
            Entity = "HrisSyncState",
            EntityId = "1",
            DetailJson = $"{{\"inserted\":{inserted},\"updated\":{updated},\"deactivated\":{deactivated}}}",
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        run.Inserted = inserted;
        run.Updated = updated;
        run.Deactivated = deactivated;
        run.Succeeded = true;
        run.FinishedAt = DateTime.UtcNow;
        Log($"Created {inserted}, updated {updated}, deactivated {deactivated}");
        Log($"Finished in {(run.FinishedAt - startedAt).TotalSeconds:0.0}s");
        run.Log = string.Join('\n', _log);
        await db.SaveChangesAsync(ct);

        if (notificationGateway != null && (inserted > 0 || updated > 0 || deactivated > 0))
        {
            try
            {
                await notificationGateway.PublishInternalAsync(
                    clientSlug: "system",
                    eventType: "system.hris_sync",
                    title: "Sinkronisasi HRIS Selesai",
                    message: $"Sinkronisasi direktori berhasil: {inserted} baru, {updated} diubah, {deactivated} nonaktif.",
                    target: new NotificationTarget(All: true),
                    priority: "low",
                    targetUrl: "/notifications/inbox",
                    ct: ct);
            }
            catch
            {
                // Non-blocking notification delivery
            }
        }

        return new HrisSyncResult(inserted, updated, deactivated, now); // callers broadcast when any count > 0
    }

    /// Best effort: a failed alert must not mask the sync failure being rethrown.
    private async Task AlertFailureAsync(string error)
    {
        if (notificationGateway is null) return;
        try
        {
            if (settings is not null && !await settings.GetAsync(SettingKeys.HrisNotifyOnFailure, true)) return;
            await notificationGateway.PublishInternalAsync(
                clientSlug: "system",
                eventType: "system.hris_sync_failed",
                title: "HRIS sync failed",
                message: error,
                target: new NotificationTarget(Role: "Admin"),
                priority: "high",
                targetUrl: "/admin");
        }
        catch
        {
            // Non-blocking notification delivery
        }
    }
}
