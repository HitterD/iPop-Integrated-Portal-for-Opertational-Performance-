using IPop.Application.Hris;
using IPop.Infrastructure.Hris;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class HrisSyncServiceTests
{
    private sealed class StubGateway(IReadOnlyList<HrisEmployee> data) : IHrisGateway
    {
        public Task<IReadOnlyList<HrisEmployee>> SyncEmployeesAsync(DateTime? updatedSince, CancellationToken ct)
            => Task.FromResult(data);
    }

    private static HrisEmployee Emp(string nik, string name, string? dept = "IT", string? parentDept = null,
        string? manager = null, bool active = true, DateTime? updatedAt = null) =>
        new(nik, name, $"{nik}@t.co", "Engineer", dept, dept is null ? null : $"Dept {dept}",
            parentDept, manager, "100", "HQ", active, updatedAt ?? new DateTime(2026, 8, 7, 8, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Sync_InsertsEmployees_WithoutDepartments_AndIsIdempotent()
    {
        using var t = new TestDb();
        var feed = new List<HrisEmployee>
        {
            Emp("20080115", "Budi Hartono", "IT"),
            Emp("20110543", "Ayu Ningrum", "IT-INFRA", parentDept: "IT", manager: "20080115"),
            Emp("20140312", "Bagas Arif", "IT-INFRA", parentDept: "IT", manager: "20110543"),
        };
        var svc = new HrisSyncService(t.Verify(), new StubGateway(feed));

        var first = await svc.RunAsync(default);
        Assert.Equal(3, first.Inserted);
        await using (var v = t.Verify())
        {
            // Departments are admin-owned now — the feed carries codes, but sync must ignore them.
            Assert.Equal(0, await v.Departments.CountAsync());
            Assert.Equal(3, await v.Users.CountAsync(u => u.DepartmentId == null));
        }

        // Second identical run must change nothing.
        var second = await new HrisSyncService(t.Verify(), new StubGateway(feed)).RunAsync(default);
        Assert.Equal(0, second.Inserted);
        await using var v2 = t.Verify();
        Assert.Equal(3, await v2.Users.CountAsync());
    }

    /// Admin edits beat the feed: a row flagged IsManuallyEdited keeps the admin's values
    /// even when HRIS reports something different on the next sync.
    [Fact]
    public async Task Sync_SkipsProfileOverwrite_WhenRowWasManuallyEdited()
    {
        using var t = new TestDb();
        await new HrisSyncService(t.Verify(), new StubGateway([Emp("20140312", "Bagas Arif")])).RunAsync(default);
        await using (var v0 = t.Verify())
        {
            var u = await v0.Users.SingleAsync(x => x.Nik == "20140312");
            u.Name = "Bagas (corrected)";
            u.IsManuallyEdited = true;
            await v0.SaveChangesAsync();
        }

        await new HrisSyncService(t.Verify(), new StubGateway([Emp("20140312", "Bagas A. Renamed")])).RunAsync(default);

        await using var v1 = t.Verify();
        Assert.Equal("Bagas (corrected)", (await v1.Users.SingleAsync(x => x.Nik == "20140312")).Name);
    }

    [Fact]
    public async Task Sync_CircularManagerReference_DoesNotDeadlock()
    {
        using var t = new TestDb();
        var feed = new List<HrisEmployee>
        {
            Emp("A1", "Alpha", "D", manager: "B1"),
            Emp("B1", "Beta", "D", manager: "A1"),
        };
        var r = await new HrisSyncService(t.Verify(), new StubGateway(feed)).RunAsync(default);
        Assert.Equal(2, r.Inserted);
        await using var v = t.Verify();
        Assert.Equal("B1", (await v.Users.SingleAsync(u => u.Nik == "A1")).ManagerNik);
        Assert.Equal("A1", (await v.Users.SingleAsync(u => u.Nik == "B1")).ManagerNik);
    }

    [Fact]
    public async Task Sync_Deactivation_SoftDeletes_AndRevokesSessions()
    {
        using var t = new TestDb();
        // First sync: active employee.
        await new HrisSyncService(t.Verify(), new StubGateway([Emp("20140312", "Bagas Arif")]))
            .RunAsync(default);
        await using var v0 = t.Verify();
        var user = await v0.Users.SingleAsync(u => u.Nik == "20140312");
        v0.AuthSessions.Add(new Core.Entities.AuthSession
        {
            UserId = user.Id, RefreshTokenHash = "hash-1", CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        });
        await v0.SaveChangesAsync();

        // Second sync: same employee now inactive.
        var r = await new HrisSyncService(t.Db,
            new StubGateway([Emp("20140312", "Bagas Arif", active: false)])).RunAsync(default);

        Assert.Equal(1, r.Deactivated);
        await using var v1 = t.Verify();
        Assert.False((await v1.Users.SingleAsync(u => u.Nik == "20140312")).IsActive);
        Assert.NotNull((await v1.AuthSessions.SingleAsync(s => s.UserId == user.Id)).RevokedAt);
    }

    [Fact]
    public async Task Sync_NeverTouchesAuthFields()
    {
        using var t = new TestDb();
        await new HrisSyncService(t.Verify(), new StubGateway([Emp("20140312", "Bagas Arif")])).RunAsync(default);
        await using var v0 = t.Verify();
        var u = await v0.Users.SingleAsync(x => x.Nik == "20140312");
        u.PasswordHash = "salt$hash"; u.MustChangePassword = false; u.FailedLoginCount = 4;
        u.LockedUntil = DateTime.UtcNow.AddMinutes(5);
        await v0.SaveChangesAsync();

        // HRIS renames the person — profile fields update, auth fields survive.
        await new HrisSyncService(t.Verify(), new StubGateway([Emp("20140312", "Bagas A. Renamed")])).RunAsync(default);

        await using var v1 = t.Verify();
        var after = await v1.Users.SingleAsync(x => x.Nik == "20140312");
        Assert.Equal("Bagas A. Renamed", after.Name);
        Assert.Equal("salt$hash", after.PasswordHash);
        Assert.False(after.MustChangePassword);
        Assert.Equal(4, after.FailedLoginCount);
        Assert.NotNull(after.LockedUntil);
    }

    [Fact]
    public async Task Sync_NeverTouchesPresenceFacts()
    {
        using var t = new TestDb();
        await new HrisSyncService(t.Verify(), new StubGateway([Emp("20140312", "Bagas Arif")])).RunAsync(default);
        var seen = DateTime.UtcNow.AddMinutes(-2);
        await using (var v0 = t.Verify())
        {
            var u = await v0.Users.SingleAsync(x => x.Nik == "20140312");
            u.PresenceLastSeenAt = seen;
            u.PresenceManualStatus = "busy";
            await v0.SaveChangesAsync();
        }

        await new HrisSyncService(t.Verify(), new StubGateway([Emp("20140312", "Bagas Renamed")])).RunAsync(default);

        await using var v1 = t.Verify();
        var after = await v1.Users.SingleAsync(x => x.Nik == "20140312");
        Assert.Equal(seen, after.PresenceLastSeenAt);
        Assert.Equal("busy", after.PresenceManualStatus);
    }

    /// The live feed carries ~50 repeated NIKs across its 4748 rows. Without collapsing them
    /// the service queues two inserts for one NIK and the unique index fails the whole run.
    [Fact]
    public async Task Sync_DuplicateNikInFeed_KeepsFreshestRow()
    {
        using var t = new TestDb();
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var feed = new List<HrisEmployee>
        {
            Emp("00000024", "Stale Name", updatedAt: older),
            Emp("00000024", "Current Name", updatedAt: newer),
        };

        var r = await new HrisSyncService(t.Verify(), new StubGateway(feed)).RunAsync(default);

        Assert.Equal(1, r.Inserted);
        await using var v = t.Verify();
        Assert.Equal("Current Name", (await v.Users.SingleAsync(u => u.Nik == "00000024")).Name);
    }

    [Fact]
    public async Task Sync_AdvancesWatermark_AndAudits()
    {
        using var t = new TestDb();
        await new HrisSyncService(t.Verify(), new StubGateway([Emp("20140312", "Bagas Arif")])).RunAsync(default);

        await using var v = t.Verify();
        var state = await v.HrisSyncState.SingleAsync(s => s.Id == 1);
        Assert.Equal(new DateTime(2026, 8, 7, 8, 0, 0, DateTimeKind.Utc), state.LastUpdated);
        Assert.NotNull(state.LastFullSyncAt);
        Assert.Contains(await v.AuditLogs.ToListAsync(), a => a.Action == "hris.sync");
    }
}
