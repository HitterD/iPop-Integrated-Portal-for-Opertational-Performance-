using System.Collections.Concurrent;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IPop.Infrastructure.Rbac;

/// Short-lived cache of permission answers. Every API request checks at least one permission (4-6 queries each);
/// with ~1000 users that was the largest share of database load in the 2026-11-09 load test.
///
/// Correctness: any SaveChanges that touches roles, role permissions, user roles or per-user overrides bumps a
/// global version (RbacChangeInterceptor), which empties the cache at once — an admin change applies on the very
/// next request, not after the TTL. The TTL only bounds staleness for changes made outside EF (none today).
public sealed class PermissionCache
{
    public static TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<(Guid, string, string?), (bool Allowed, long Version, DateTime At)> answers = new();
    private long version;

    public void Invalidate() { Interlocked.Increment(ref version); answers.Clear(); }

    public bool TryGet(Guid userId, string code, string? dept, DateTime now, out bool allowed)
    {
        allowed = false;
        if (!answers.TryGetValue((userId, code, dept), out var hit)) return false;
        if (hit.Version != Interlocked.Read(ref version) || now - hit.At > Ttl) return false;
        allowed = hit.Allowed;
        return true;
    }

    public void Set(Guid userId, string code, string? dept, bool allowed, DateTime now)
    {
        if (answers.Count > 50_000) answers.Clear();   // bound memory: ~1000 users × a few dozen codes
        answers[(userId, code, dept)] = (allowed, Interlocked.Read(ref version), now);
    }
}

/// Empties the permission cache whenever an RBAC table changes through EF: once before the save (so no request
/// re-caches the old answer from a stale read) and again after it commits (a request that read between the two).
public sealed class RbacChangeInterceptor(PermissionCache cache) : SaveChangesInterceptor
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Microsoft.EntityFrameworkCore.DbContext, object> pending = new();

    private void Before(DbContextEventData e)
    {
        if (e.Context is not { } ctx) return;
        var touches = ctx.ChangeTracker.Entries().Any(x =>
            x.State is Microsoft.EntityFrameworkCore.EntityState.Added or Microsoft.EntityFrameworkCore.EntityState.Modified
                or Microsoft.EntityFrameworkCore.EntityState.Deleted
            && x.Entity is Role or RolePermission or UserRole or UserPermissionOverride or Permission);
        if (!touches) return;
        cache.Invalidate();
        pending.AddOrUpdate(ctx, ctx);
    }

    private void After(DbContextEventData e)
    {
        if (e.Context is { } ctx && pending.Remove(ctx)) cache.Invalidate();
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData e, InterceptionResult<int> result)
    { Before(e); return result; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> result, CancellationToken ct = default)
    { Before(e); return ValueTask.FromResult(result); }

    public override int SavedChanges(SaveChangesCompletedEventData e, int result) { After(e); return result; }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData e, int result, CancellationToken ct = default)
    { After(e); return ValueTask.FromResult(result); }
}
