using IPop.Application.Auth;
using IPop.Application.Hris;
using IPop.Core.Entities;
using IPop.Infrastructure.Auth;
using IPop.Infrastructure.Hris;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace IPop.Tests;

/// One end-to-end pass over the real services (SQLite in-memory standing in for
/// PostgreSQL): HRIS sync → default-password login → forced change → every old
/// session dead → new password signs in. Run on its own with:
///   dotnet test --filter "FullyQualifiedName~SelfCheck"
public class SelfCheck(ITestOutputHelper output)
{
    private sealed class StubGateway(IReadOnlyList<HrisEmployee> data) : IHrisGateway
    {
        public Task<IReadOnlyList<HrisEmployee>> SyncEmployeesAsync(DateTime? s, CancellationToken ct)
            => Task.FromResult(data);
    }

    [Fact]
    public async Task FullLifecycle_SyncToLoginToChangeToSessionDeath()
    {
        var step = (string s) => output.WriteLine($"✓ {s}");

        // HRIS pushes one employee.
        var feed = new List<HrisEmployee>
        {
            new("20140312", "Bagas Arif", "bagas@t.co", "Infra Engineer", "IT-INFRA",
                "Infrastructure", "IT", "20110543", "114", "HQ · F3", true,
                new DateTime(2026, 8, 7, 8, 0, 0, DateTimeKind.Utc)),
        };

        using var t = new TestDb();
        AuthWorkflow Auth() => new(t.Verify(), new Argon2PasswordHasher(),
            new JwtTokenService(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 64) })
                .Build()));

        // 1. Sync inserts the employee with the default-password state.
        var sync = await new HrisSyncService(t.Verify(), new StubGateway(feed)).RunAsync(default);
        Assert.Equal(1, sync.Inserted);
        step($"sync inserted {sync.Inserted} employee");

        // 2. Default password signs in and demands a change.
        var login = (await Auth().LoginAsync(new LoginRequest("20140312", "1234", "self-check"), null, default)).Item1;
        Assert.NotNull(login);
        Assert.True(login!.MustChangePassword);
        step("default password accepted, MustChangePassword=true");

        // 3. Change to a compliant password.
        var (err, status) = await Auth().ChangePasswordAsync("20140312",
            new ChangePasswordRequest("MySecure9", "MySecure9"), null, default);
        Assert.Equal(200, status);
        step("password changed");

        // 4. The pre-change refresh token is dead.
        var refresh = await Auth().RefreshAsync(new RefreshRequest(login.RefreshToken), null, default);
        Assert.Equal(401, refresh.Item3);
        step("old refresh token rejected (global invalidation)");

        // 5. New password signs in without the change flag.
        var relogin = (await Auth().LoginAsync(new LoginRequest("20140312", "MySecure9", "self-check"), null, default)).Item1;
        Assert.NotNull(relogin);
        Assert.False(relogin!.MustChangePassword);
        step("new password signs in, flag cleared");

        // 6. Presence heartbeat is server-stamped and survives a directory read.
        var presenceUser = await t.Verify().Users.SingleAsync(u => u.Nik == "20140312");
        var now = DateTime.UtcNow;
        await using (var v = t.Verify())
        {
            var u = await v.Users.SingleAsync(x => x.Nik == "20140312");
            u.PresenceLastSeenAt = now;
            await v.SaveChangesAsync();
        }
        await using var presenceRead = t.Verify();
        var directoryUser = await presenceRead.Users.AsNoTracking().SingleAsync(u => u.Nik == "20140312");
        Assert.Equal("on", IPop.Core.Presence.PresenceStatus.ComputeStatus(
            directoryUser.PresenceLastSeenAt, directoryUser.PresenceManualStatus, now));
        step("presence beat fact produces online directory status");

        output.WriteLine("SELF-CHECK PASSED");
    }
}
