using IPop.Application.Hris;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Hris;

/// Sync upserts Users only. Departments and sites are owned by the admin, not HRIS —
/// the feed's department codes are ignored entirely (they never set DepartmentId).
/// HRIS updatedAt wins for profile fields unless the row was edited by an admin
/// (IsManuallyEdited); auth fields (password_hash, must_change_password,
/// failed_login_count, locked_until) and presence facts are never touched;
/// statusAktif=false → soft deactivate + revoke sessions.
public class HrisSyncService(IPopDbContext db, IHrisGateway gateway, INotificationGatewayService? notificationGateway = null) : IHrisSyncService
{
    public async Task<HrisSyncResult> RunAsync(CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var run = new HrisSyncRun { Id = Guid.NewGuid(), StartedAt = startedAt };
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
            try { await db.SaveChangesAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    private async Task<HrisSyncResult> RunCoreAsync(HrisSyncState state, HrisSyncRun run,
        DateTime startedAt, CancellationToken ct)
    {
        var now = startedAt;

        // The feed can carry the same NIK twice — duplicate source rows, or a page boundary
        // shifting under a paginated pull. `users` below is read once, before the insert loop,
        // so a repeated NIK would queue two inserts and break the unique index for the whole
        // run. Collapse to the freshest record per NIK.
        var employees = (await gateway.SyncEmployeesAsync(state.LastUpdated, ct))
            .GroupBy(e => e.Nik)
            .Select(g => g.MaxBy(e => e.UpdatedAt)!)
            .ToList();

        var inserted = 0;
        var updated = 0;
        var deactivated = 0;

        var users = await db.Users.ToDictionaryAsync(u => u.Nik, ct);
        foreach (var e in employees)
        {
            if (users.TryGetValue(e.Nik, out var u))
            {
                var changed = false;
                if (!u.IsManuallyEdited)
                {
                    changed = u.Name != e.Nama || u.Email != e.Email || u.Position != e.Jabatan
                        || u.ManagerNik != e.NikAtasan || u.Extension != e.Ekstensi || u.Location != e.Lokasi;
                    if (changed)
                    {
                        u.Name = e.Nama;
                        u.Email = e.Email;
                        u.Position = e.Jabatan;
                        u.ManagerNik = e.NikAtasan;
                        u.Extension = e.Ekstensi;
                        u.Location = e.Lokasi;
                        u.UpdatedAt = now;
                    }
                }

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
}
